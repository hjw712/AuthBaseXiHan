using System.Text.RegularExpressions;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions.Ai;
using AuthBaseXiHan.Sessions.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XiHan.Framework.AI.Abstractions.Chat;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;
// 框架的对话请求类型与本项目的 AiChatRequest 只差大小写，用别名避免读错
using FrameworkChatRequest = XiHan.Framework.AI.Abstractions.Chat.AIChatRequest;

namespace AuthBaseXiHan.Operations;

// 模型配置管理：维护 LlmProviderConfig 表（Endpoint / Key / Model 入库，Key 加密）
// 全部需要 llm.admin 策略；TenantId 从安全上下文取，不接收客户端参数

#region 配置列表
/// <summary>
/// 模型配置列表：契约 ID llm.configs → GET /llm:configs（分页 + 关键字筛选）。
/// 请求/响应复用通用分页契约（Operations/Paging.cs）：SessionPagedRequest / SessionPagedResponse&lt;LlmConfigItem&gt;，
/// 分页参数由 Paging.Validate 按 "Paging" 配置节校验（DefaultPageSize / MaxPageSize / MaxPage 全局可配）。
/// Query 筛选：对别名做包含匹配（通配符转义按字面处理）。
/// ApiKey 只回掩码，明文永不外泄
/// </summary>
[Operation(LlmConfigsOperation.Id, Expose = Expose.Http, ReadOnly = true,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class LlmConfigsOperation(AuthDbContext db, IOptions<PagingOptions> paging)
    : IOperation<SessionPagedRequest, SessionPagedResponse<LlmConfigItem>>
{
    public const string Id = "llm.configs";

    public async ValueTask<Result<SessionPagedResponse<LlmConfigItem>>> HandleAsync(
        SessionPagedRequest request, InvocationContext context)
    {
        // 分页参数校验与规范化（默认页大小/上限来自 Paging 配置）
        var pagingResult = Paging.Validate(request, paging.Value);
        if (!pagingResult.TryGetValue(out var pagingValues))
        {
            return Result.Failure<SessionPagedResponse<LlmConfigItem>>(pagingResult.Error!);
        }
        var (page, pageSize) = pagingValues;

        var tenantId = context.CurrentTenantId();
        var query = db.LlmConfigs.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .AsQueryable();

        // Query 关键字筛选：别名包含匹配（SQL Server 默认排序规则不区分大小写）
        var keyword = request.Query?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = "%" + Paging.EscapeLike(keyword) + "%";
            query = query.Where(c => EF.Functions.Like(c.Alias, pattern));
        }

        // 总数单独查（筛选后、不带排序与投影），items 再 Skip/Take——前端需要 total 渲染页码
        var total = await query.LongCountAsync(context.CancellationToken);
        var items = await query
            .OrderBy(c => c.Alias)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new LlmConfigItem(
                c.Id, c.Alias, c.Provider, c.Endpoint,
                // 掩码在数据库侧拼：明文根本不离开实体查询，避免中间变量泄漏
                c.ApiKeyEncrypted.Length == 0 ? string.Empty : "sk-****",
                c.Model, c.SystemPrompt, c.IsActive, c.UpdatedAt))
            .ToListAsync(context.CancellationToken);

        return Result.Success(new SessionPagedResponse<LlmConfigItem>(items, total, page, pageSize));
    }
}

/// <summary>配置项（ApiKey 已掩码）</summary>
/// <param name="ConfigId">配置 ID</param>
/// <param name="Alias">业务别名</param>
/// <param name="Provider">供应商类型</param>
/// <param name="Endpoint">接口地址</param>
/// <param name="ApiKeyMasked">密钥掩码</param>
/// <param name="Model">模型名</param>
/// <param name="SystemPrompt">系统提示词（null 表示未配置）</param>
/// <param name="IsActive">是否启用</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record LlmConfigItem(long ConfigId, string Alias, string Provider, string Endpoint,
    string ApiKeyMasked, string Model, string? SystemPrompt, bool IsActive, DateTimeOffset UpdatedAt);
#endregion

#region 新增或更新
/// <summary>
/// 新增或更新模型配置：契约 ID llm.upsertconfig → POST /llm:upsertconfig
/// ApiKey 加密后入库；不传 ApiKey 则保留原值；可同时配置系统提示词；更新后清缓存立即生效
/// </summary>
[Operation(LlmConfigUpsertOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class LlmConfigUpsertOperation(
    AuthDbContext db, LlmKeyProtector protector, DbAIProviderCatalog catalog)
    : IOperation<LlmConfigUpsertRequest, LlmConfigUpsertResponse>
{
    public const string Id = "llm.upsertconfig";

    public async ValueTask<Result<LlmConfigUpsertResponse>> HandleAsync(
        LlmConfigUpsertRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();

        // 字段校验
        if (!Regex.IsMatch(request.Alias, @"^[a-zA-Z0-9_-]{1,50}$"))
        {
            return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_001", "llm.alias_invalid")
                .WithText("别名须为 1-50 位字母、数字、下划线或中划线"));
        }
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && IsPrivateHost(uri))))
        {
            return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_002", "llm.endpoint_invalid")
                .WithText("接口地址须为 HTTPS（本机或内网私有地址允许 http://）"));
        }
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_003", "llm.model_empty")
                .WithText("模型名不能为空"));
        }
        // 提示词可选；传了但超过 4000 字符拒绝（与库列长度一致）
        if (request.SystemPrompt is { Length: > 4000 })
        {
            return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_006", "llm.prompt_too_long")
                .WithText("系统提示词不能超过 4000 字符"));
        }

        var existing = await db.LlmConfigs
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Alias == request.Alias, context.CancellationToken);
        var now = DateTimeOffset.UtcNow;

        LlmProviderConfig entity;
        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_004", "llm.key_required")
                    .WithText("新增配置时 API Key 不能为空"));
            }

            entity = new LlmProviderConfig
            {
                TenantId = tenantId,
                Alias = request.Alias,
                Provider = request.Provider,
                Endpoint = request.Endpoint,
                ApiKeyEncrypted = protector.Protect(request.ApiKey),
                Model = request.Model,
                SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
                IsActive = request.IsActive,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.LlmConfigs.Add(entity);
        }
        else
        {
            existing.Provider = request.Provider;
            existing.Endpoint = request.Endpoint;
            existing.Model = request.Model;
            existing.IsActive = request.IsActive;
            existing.UpdatedAt = now;
            // 提示词：不传保留原值；显式传空串/空白表示清空
            if (request.SystemPrompt is not null)
            {
                existing.SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt;
            }
            // 不传 ApiKey 表示保留原值；显式传空串表示清空（后续调用会解密失败，符合预期）
            if (request.ApiKey is not null)
            {
                existing.ApiKeyEncrypted = protector.Protect(request.ApiKey);
            }

            entity = existing;
        }

        // 直接改内存目录，**不回查数据库**：写库由框架工作单元在本操作返回后才提交，
        // 此刻查库读到的是旧行，会把刚写入的配置覆盖回旧值。
        catalog.Apply(entity);

        return Result.Success(new LlmConfigUpsertResponse(entity.Id, request.Alias, true));
    }

    /// <summary>
    /// 是否本机或内网私有地址：HTTP 明文只允许发往这些主机，公网必须用 HTTPS。
    /// </summary>
    /// <remarks>
    /// 目的很具体：API Key 走明文 HTTP 时，链路上任何一跳都能看到它。
    /// 本机回环与内网私有网段（RFC 1918 / 链路本地 / IPv6 ULA）里的流量不经过公网，
    /// 复用同一套加密（TLS）收益很低，而自建模型网关、内网 vLLM/Ollama 这类部署几乎都是 http，
    /// 一刀切要求 HTTPS 会把它们挡在录入接口之外，逼使用者绕过接口直接写库——那样反而更不安全。
    ///
    /// 判定按 IP 字面量做，不做 DNS 解析：解析结果随时可能变（DNS 重绑定），
    /// 拿解析后的地址放行等于给「先指向内网、后指向公网」留下窗口。
    /// 因此以主机名形式填写的内网地址仍须走 HTTPS——这是有意的保守取舍。
    /// </remarks>
    private static bool IsPrivateHost(Uri uri)
    {
        if (uri.IsLoopback) return true;

        if (!System.Net.IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10                                   // 10.0.0.0/8
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) // 172.16.0.0/12
                || (bytes[0] == 192 && bytes[1] == 168)             // 192.168.0.0/16
                || (bytes[0] == 169 && bytes[1] == 254);            // 169.254.0.0/16 链路本地
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;  // fe80::/10 / fc00::/7 ULA
        }

        return false;
    }
}

/// <summary>新增或更新模型配置请求</summary>
/// <param name="Alias">业务别名，1-50 位字母数字下划线中划线</param>
/// <param name="Provider">供应商类型：OpenAICompatible / AzureOpenAI / DashScope</param>
/// <param name="Endpoint">接口地址，如 https://api.deepseek.com/v1</param>
/// <param name="ApiKey">API Key；新增必填，更新时不传保留原值</param>
/// <param name="Model">模型名，如 deepseek-chat</param>
/// <param name="SystemPrompt">系统提示词（≤4000 字符）；更新时不传保留原值，传空串清空</param>
/// <param name="IsActive">是否启用，默认 true</param>
public sealed record LlmConfigUpsertRequest(string Alias, string Provider, string Endpoint,
    string? ApiKey, string Model, string? SystemPrompt = null, bool IsActive = true);

/// <summary>新增或更新响应</summary>
/// <param name="ConfigId">配置 ID（新增时框架提交后才生成，此处为占位）</param>
/// <param name="Alias">业务别名</param>
/// <param name="Updated">是否已更新</param>
public sealed record LlmConfigUpsertResponse(long ConfigId, string Alias, bool Updated);
#endregion

#region 连通性测试
/// <summary>
/// 模型连通性测试：契约 ID llm.testconfig → POST /llm:testconfig
/// 用已存配置发一次极简请求，不写库、Key 不回显
/// </summary>
[Operation(LlmConfigTestOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class LlmConfigTestOperation(
    AuthDbContext db, IAIChat aiChat, DbAIProviderCatalog catalog)
    : IOperation<LlmConfigTestRequest, LlmConfigTestResponse>
{
    public const string Id = "llm.testconfig";

    public async ValueTask<Result<LlmConfigTestResponse>> HandleAsync(
        LlmConfigTestRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();
        await catalog.EnsureLoadedAsync(db, context.CancellationToken);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reply = await aiChat.CompleteAsync(new FrameworkChatRequest
        {
            Provider = catalog.ProviderName(tenantId, request.Alias),
            Messages = [new ChatMessage(ChatRole.User, "ping")],
        }, context.CancellationToken);

        if (reply.IsFailure)
        {
            sw.Stop();
            // 配置加载失败（没录入/Key 解不开）与上游失败都汇总成 Success=false + 错误码，
            // 这个接口的用途就是「告诉我哪儿不对」，所以不把失败包装成 HTTP 错误
            var error = reply.Error!;
            return Result.Success(new LlmConfigTestResponse(false, sw.ElapsedMilliseconds, null,
                $"{error.Code}: {error.Text ?? error.MessageKey}"));
        }

        sw.Stop();
        reply.TryGetValue(out var response);
        return Result.Success(new LlmConfigTestResponse(true, sw.ElapsedMilliseconds,
            Truncate(response?.Text ?? string.Empty, 100), null));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

/// <summary>连通性测试请求</summary>
/// <param name="Alias">已存配置的别名</param>
public sealed record LlmConfigTestRequest(string Alias);

/// <summary>连通性测试响应</summary>
/// <param name="Success">是否成功</param>
/// <param name="LatencyMs">耗时毫秒</param>
/// <param name="ModelReply">模型回复（截断）</param>
/// <param name="Error">失败原因</param>
public sealed record LlmConfigTestResponse(bool Success, long LatencyMs, string? ModelReply, string? Error);
#endregion

#region 删除
/// <summary>
/// 删除模型配置：契约 ID llm.deleteconfig → POST /llm:deleteconfig
/// </summary>
[Operation(LlmConfigDeleteOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class LlmConfigDeleteOperation(AuthDbContext db, DbAIProviderCatalog catalog)
    : IOperation<LlmConfigDeleteRequest, LlmConfigDeleteResponse>
{
    public const string Id = "llm.deleteconfig";

    public async ValueTask<Result<LlmConfigDeleteResponse>> HandleAsync(
        LlmConfigDeleteRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();
        var entity = await db.LlmConfigs.FirstOrDefaultAsync(
            c => c.Id == request.ConfigId && c.TenantId == tenantId, context.CancellationToken);

        if (entity is null)
        {
            return Result.Failure<LlmConfigDeleteResponse>(
                Error.NotFound("LLM_NOTFOUND_001", "llm.config_not_found").WithText("模型配置不存在"));
        }

        db.LlmConfigs.Remove(entity);
        // 同步摘除内存目录：否则本进程会继续拿已删除的配置服务请求，直到快照过期
        catalog.Remove(tenantId, entity.Alias);
        return Result.Success(new LlmConfigDeleteResponse(true));
    }
}

/// <summary>删除配置请求</summary>
/// <param name="ConfigId">配置 ID</param>
public sealed record LlmConfigDeleteRequest(long ConfigId);

/// <summary>删除配置响应</summary>
/// <param name="Deleted">是否已删除</param>
public sealed record LlmConfigDeleteResponse(bool Deleted);
#endregion
