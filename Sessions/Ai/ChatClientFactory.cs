using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using AuthBaseXiHan.Persistence;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>
/// 按租户 + Alias 动态构建 IChatClient 的工厂。
/// </summary>
/// <remarks>
/// 替代 appsettings 静态注册：配置从数据库读、Key 经 Data Protection 解密、客户端建好后缓存。
/// 管理端 upsert 配置后调用 <see cref="InvalidateAsync"/> 清缓存，下次调用自动用新配置，无需重启。
///
/// 安全取舍：解密后的明文 Key 与客户端快照只存在进程内存（ConcurrentDictionary，带 10 分钟 TTL），
/// 不写 Redis——避免明文 Key 落到共享缓存被其他服务/运维直接读到。
/// 同进程的配置变更走 InvalidateAsync 立即生效；跨实例/直改数据库靠 TTL 兜底收敛。
/// </remarks>
public interface IChatClientFactory
{
    /// <summary>按租户与别名取得模型客户端</summary>
    ValueTask<IChatClient> CreateAsync(string alias, string tenantId, CancellationToken cancellationToken);

    /// <summary>按租户与别名取得模型客户端与配置的系统提示词（无提示词返回 null）</summary>
    ValueTask<(IChatClient Client, string? SystemPrompt)> CreateWithPromptAsync(
        string alias, string tenantId, CancellationToken cancellationToken);

    /// <summary>清除缓存（配置变更后调用）</summary>
    ValueTask InvalidateAsync(string alias, string tenantId, CancellationToken cancellationToken);

    /// <summary>加密 API Key（配置写入时调用）</summary>
    byte[] ProtectApiKey(string plainApiKey);

    /// <summary>取配置明文用于连通性测试（不落库、不回显）</summary>
    ValueTask<ClientSnapshot?> GetSnapshotAsync(string alias, string tenantId, CancellationToken cancellationToken);

    /// <summary>配置快照：Provider / Endpoint / ApiKey / Model / SystemPrompt</summary>
    public sealed record ClientSnapshot(string Provider, string Endpoint, string ApiKey, string Model, string? SystemPrompt = null);
}

/// <inheritdoc />
public sealed class ChatClientFactory(
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider protectionProvider) : IChatClientFactory
{
    /// <summary>快照缓存有效期：超时后重新查库，跨实例/直改数据库的配置变更最迟在此时间内生效</summary>
    private static readonly TimeSpan SnapshotTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>缓存 key：(租户, 别名) → (客户端快照, 缓存时刻)</summary>
    private readonly ConcurrentDictionary<(string TenantId, string Alias), (IChatClientFactory.ClientSnapshot Snapshot, DateTimeOffset CachedAt)> _snapshots = new();

    /// <summary>专用用途串：不同用途的密文不能互相解，泄漏一个用途的 key 不会波及别处</summary>
    private IDataProtector Protector => protectionProvider.CreateProtector("AuthBaseXiHan.LlmProviderConfig.ApiKey.v1");

    /// <inheritdoc />
    public async ValueTask<IChatClient> CreateAsync(string alias, string tenantId, CancellationToken cancellationToken)
    {
        var (client, _) = await CreateWithPromptAsync(alias, tenantId, cancellationToken);
        return client;
    }

    /// <inheritdoc />
    public async ValueTask<(IChatClient Client, string? SystemPrompt)> CreateWithPromptAsync(
        string alias, string tenantId, CancellationToken cancellationToken)
    {
        var cfg = await GetSnapshotAsync(alias, tenantId, cancellationToken)
                  ?? throw new InvalidOperationException($"未找到模型配置：租户 {tenantId} / 别名 {alias}。请先通过配置接口录入。");

        return (BuildClient(cfg), TrimmedPrompt(cfg.SystemPrompt));
    }

    /// <summary>提示词去空白：纯空白按无提示词处理</summary>
    private static string? TrimmedPrompt(string? prompt) =>
        string.IsNullOrWhiteSpace(prompt) ? null : prompt;

    /// <summary>从快照构建客户端（构造极廉价，无网络开销，每次 new 避免共享实例并发问题）</summary>
    private static IChatClient BuildClient(IChatClientFactory.ClientSnapshot cfg)
    {
        if (!string.Equals(cfg.Provider, "OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"暂不支持的供应商类型：{cfg.Provider}");
        }

        var options = new OpenAIClientOptions { Endpoint = new Uri(cfg.Endpoint) };
        var client = new OpenAIClient(new System.ClientModel.ApiKeyCredential(cfg.ApiKey), options);
        return client.GetChatClient(cfg.Model).AsIChatClient();
    }

    /// <inheritdoc />
    public async ValueTask<IChatClientFactory.ClientSnapshot?> GetSnapshotAsync(
        string alias, string tenantId, CancellationToken cancellationToken)
    {
        // 命中且未过 TTL 才用缓存：InvalidateAsync 只能清「处理配置变更的那个进程」的缓存，
        // 多实例部署（如本地调试 + IIS 各一份）或运维直改数据库时，其余实例靠 TTL 兜底收敛
        if (_snapshots.TryGetValue((tenantId, alias), out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < SnapshotTimeToLive)
        {
            return cached.Snapshot;
        }

        // 工厂是 Singleton，DbContext 是 Scoped：显式开一个 scope 查询，避免生命周期冲突
        LlmProviderConfig? entity;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            entity = await db.LlmConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Alias == alias && c.IsActive, cancellationToken);
        }
        if (entity is null) return null;

        string apiKey;
        try
        {
            apiKey = Encoding.UTF8.GetString(Protector.Unprotect(entity.ApiKeyEncrypted));
        }
        catch (Exception ex)
        {
            // 密钥环丢失/跨实例未共享时会走到这里：不记录明文，只记配置主键便于排查
            throw new InvalidOperationException($"模型配置 {entity.Id} 的密钥解密失败，请检查密钥环持久化配置", ex);
        }

        var snapshot = new IChatClientFactory.ClientSnapshot(
            entity.Provider, entity.Endpoint, apiKey, entity.Model, entity.SystemPrompt);
        _snapshots[(tenantId, alias)] = (snapshot, DateTimeOffset.UtcNow);
        return snapshot;
    }

    /// <inheritdoc />
    public ValueTask InvalidateAsync(string alias, string tenantId, CancellationToken cancellationToken)
    {
        _snapshots.TryRemove((tenantId, alias), out _);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public byte[] ProtectApiKey(string plainApiKey) => Protector.Protect(Encoding.UTF8.GetBytes(plainApiKey));

    /// <summary>生成掩码串（列表接口回显用，明文绝不外泄）</summary>
    public static string MaskApiKey(string plainApiKey)
    {
        if (string.IsNullOrEmpty(plainApiKey)) return string.Empty;
        var tail = plainApiKey.Length >= 4 ? plainApiKey[^4..] : plainApiKey;
        var prefix = plainApiKey.StartsWith("sk-", StringComparison.Ordinal) ? "sk-" : string.Empty;
        return $"{prefix}****{tail}";
    }
}
