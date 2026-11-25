using System.Collections.Concurrent;
using AuthBaseXiHan.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XiHan.Framework.AI.Abstractions.Providers;
using XiHan.Framework.Primitives;
// 框架的工厂契约与本项目旧类型同名，起别名避免歧义
using FrameworkClientFactory = XiHan.Framework.AI.Abstractions.Providers.IChatClientFactory;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>目录里的一条 Provider：框架的选项 + 本项目额外要用的字段</summary>
/// <param name="Options">交给框架的 Provider 选项（含解密后的 Key、超时）</param>
/// <param name="SystemPrompt">该模型的默认提示词（请求未指定 promptAlias 时使用）</param>
/// <param name="Alias">本项目的业务别名（回显给调用方用，与 Provider 名可能不同）</param>
public sealed record LlmProviderEntry(AIProviderOptions Options, string? SystemPrompt, string Alias);

/// <summary>
/// 数据库驱动的模型目录：实现框架的 <see cref="IAIProviderCatalog"/>，
/// 凭据仍来自 <see cref="LlmProviderConfig"/> 表（每租户、Key 加密），而不是配置文件。
/// </summary>
/// <remarks>
/// 为什么这样做：框架的默认目录 <c>AIProviderCatalog</c> 从 <c>XiHan:AI:Providers</c> 配置节读凭据，
/// 启动时读一次就固定，Key 只能明文写在配置文件里。本项目要的是「多租户 + Key 加密入库 + 改配置立即生效」，
/// 这正是框架文档给出的替换场景（「Provider 存在库里、在界面上设默认」时用自己的目录）。
///
/// 分工：
/// - 本类只决定**用哪些 Provider、凭据是什么**（数据来源是数据库）；
/// - 把 <see cref="AIProviderOptions"/> 变成 <see cref="IChatClient"/> 的是框架按 Kind 登记的实现
///   （<c>OpenAIFeature</c> 提供 Kind="openai"），所以供应商支持能力跟着框架走，不用自己维护客户端构造。
///
/// 同步契约的应对：<see cref="IAIProviderCatalog"/> 的方法都是同步的、不能查库，故本类持有**内存快照**：
/// 启动时全量加载；配置写入后由管理操作调用 <see cref="Apply"/> 直接改内存（**不回查数据库**——
/// 框架的工作单元在操作返回后才提交，此刻回查只会读到旧值）。
///
/// 收敛兜底：直接改数据库、或多实例下别的实例改了配置，本实例看不到。故每次对话调用前
/// 用 <see cref="EnsureLoadedAsync"/> 判一次快照年龄（只比 ticks，不查库），过有效期才重新加载。
///
/// **命名约束（踩过的坑）**：框架的 Provider 名必须是 <c>^[a-z0-9][a-z0-9._-]{0,63}$</c>——
/// 不允许 <c>/</c>、不允许多余的大写，而本项目的租户与别名比这宽松（别名允许大写与下划线）。
/// 因此不能简单拼 <c>租户/别名</c>，而是：由租户+别名**生成**一个合规名（非法字符转 <c>_</c>、统一小写），
/// 并在加载期维护 <c>(租户,别名) → 名字</c> 的映射表；同名冲突时追加 <c>-2</c>、<c>-3</c> 消歧。
/// 映射表也让「解析」不必反解字符串，避免分隔符在租户/别名里出现时歧义。
/// </remarks>
public sealed class DbAIProviderCatalog : IAIProviderCatalog
{
    /// <summary>快照有效期：跨实例 / 直改数据库的配置变更最迟在此时间内被本实例看到</summary>
    private static readonly TimeSpan SnapshotTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>框架 Provider 名的最大长度</summary>
    private const int MaxNameLength = 64;

    /// <summary>Kind → 框架登记的客户端工厂（OpenAIFeature 提供 "openai"）</summary>
    private readonly IReadOnlyDictionary<string, FrameworkClientFactory> _factories;

    private readonly LlmKeyProtector _protector;
    private readonly IOptions<AiChatOptions> _options;
    private readonly ILogger<DbAIProviderCatalog> _logger;

    /// <summary>Provider 名 → 目录条目（不可变字典，整体替换实现无锁读）</summary>
    private volatile IReadOnlyDictionary<string, LlmProviderEntry> _entries =
        new Dictionary<string, LlmProviderEntry>(StringComparer.Ordinal);

    /// <summary>(租户, 别名) → 合规的 Provider 名；由加载/写入时维护，解析路径不必反解字符串</summary>
    private volatile IReadOnlyDictionary<(string TenantId, string Alias), string> _names =
        new Dictionary<(string, string), string>();

    /// <summary>配置损坏的 Provider 名 → 原因（解密失败等）：解析时报可诊断的错误，不反复重试</summary>
    private volatile IReadOnlyDictionary<string, string> _broken =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>已构建的客户端缓存（框架契约要求按名字缓存，同名并发解析只建一个实例）</summary>
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);

    /// <summary>快照加载时刻（UTC ticks）；0 表示尚未加载</summary>
    private long _loadedAtTicks;

    public DbAIProviderCatalog(
        IEnumerable<FrameworkClientFactory> factories,
        LlmKeyProtector protector,
        IOptions<AiChatOptions> options,
        ILogger<DbAIProviderCatalog> logger)
    {
        // 同一种类登记两次以最后登记的为准（与框架约定一致）
        _factories = factories
            .GroupBy(f => f.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        _protector = protector;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// 取某个 (租户, 别名) 在框架里的 Provider 名。
    /// 已加载时读映射表（能拿到冲突消歧后的真名）；未加载时按同一规则现算，保证可用。
    /// </summary>
    public string ProviderName(string tenantId, string alias) =>
        _names.TryGetValue((tenantId, alias), out var name) ? name : ComposeName(tenantId, alias);

    /// <summary>由租户与别名生成合规名：非法字符转下划线、统一小写、首字符补足、截断到 64</summary>
    private static string ComposeName(string tenantId, string alias)
    {
        var raw = tenantId + "." + alias;
        var buffer = new char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var c = char.ToLowerInvariant(raw[i]);
            buffer[i] = c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '_';
        }

        var name = new string(buffer);
        // 首字符必须是字母或数字：不合规时前置 p，避免整条配置因命名被判非法
        if (name.Length == 0 || name[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9'))
        {
            name = "p" + name;
        }

        // 留出冲突后缀的空间（-2 之类）
        return name.Length <= MaxNameLength ? name : name[..MaxNameLength];
    }

    // ── IAIProviderCatalog ──────────────────────────────────────────────

    /// <inheritdoc />
    public IReadOnlyCollection<string> Providers => [.. _entries.Keys];

    /// <summary>
    /// 默认 Provider：本项目的调用方总是显式传 Provider（别名来自请求），故不给默认。
    /// 返回 null 时框架报 AI_NOTFOUND_001，比猜一个更诚实。
    /// </summary>
    public string? DefaultProvider => null;

    /// <summary>本项目未接入知识库/嵌入，嵌入也没有默认</summary>
    public string? DefaultEmbeddingProvider => null;

    /// <inheritdoc />
    public Maybe<AIProviderOptions> Find(string name) =>
        _entries.TryGetValue(name, out var entry)
            ? Maybe.Some(entry.Options)
            : Maybe.None<AIProviderOptions>();

    /// <summary>取某模型的默认提示词（请求未指定 promptAlias 时的兜底）；无则 null</summary>
    public string? SystemPromptOf(string name) =>
        _entries.TryGetValue(name, out var entry) && !string.IsNullOrWhiteSpace(entry.SystemPrompt)
            ? entry.SystemPrompt
            : null;

    /// <summary>取某模型的业务别名（错误文案回显用）；无则 null</summary>
    public string? AliasOf(string name) =>
        _entries.TryGetValue(name, out var entry) ? entry.Alias : null;

    /// <inheritdoc />
    public Result<IChatClient> ResolveChatClient(string? name)
    {
        var resolved = string.IsNullOrWhiteSpace(name) ? DefaultProvider : name;
        if (resolved is null)
        {
            return Result.Failure<IChatClient>(Error.NotFound("AI_NOTFOUND_001", "ai.provider.default_missing"));
        }

        if (_clients.TryGetValue(resolved, out var cached))
        {
            return Result.Success(cached);
        }

        if (_broken.TryGetValue(resolved, out var reason))
        {
            // 配置存在但当前用不了（典型是密钥环不匹配）：503 而不是 404
            return Result.Failure<IChatClient>(Error.Unavailable("AI_UNAVAILABLE_002", "ai.config_broken")
                .WithText(reason));
        }

        if (!_entries.TryGetValue(resolved, out var entry))
        {
            // 这里不知道业务别名（配置根本不存在），所以不拼名字——由知道别名的调用方补文案
            return Result.Failure<IChatClient>(Error.NotFound("AI_NOTFOUND_001", "ai.provider.not_found")
                .WithText("模型配置不存在或未启用"));
        }

        if (!_factories.TryGetValue(entry.Options.Kind, out var factory))
        {
            return Result.Failure<IChatClient>(Error.NotFound("AI_NOTFOUND_001", "ai.provider.kind_not_found")
                .WithText($"没有登记种类为 {entry.Options.Kind} 的客户端工厂"));
        }

        var created = factory.Create(entry.Options);
        if (created.IsFailure)
        {
            return created;
        }

        if (!created.TryGetValue(out var client))
        {
            return Result.Failure<IChatClient>(Error.Unexpected("AI_UNEXPECTED_001", "ai.provider.create_failed"));
        }

        // 同名并发解析只留一个实例：GetOrAdd 保证竞态下也只暴露一个
        return Result.Success(_clients.GetOrAdd(resolved, client));
    }

    /// <inheritdoc />
    public Result<IEmbeddingGenerator<string, Embedding<float>>> ResolveEmbeddingGenerator(string? name) =>
        // 知识库/嵌入尚未接入：明确报未配置，而不是假装支持
        Result.Failure<IEmbeddingGenerator<string, Embedding<float>>>(
            Error.NotFound("AI_NOTFOUND_001", "ai.provider.embedding_default_missing"));

    /// <inheritdoc />
    public void Invalidate(IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            _clients.TryRemove(name, out _);
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        _clients.Clear();
        Interlocked.Exchange(ref _loadedAtTicks, 0);
    }

    // ── 本项目扩展：快照维护 ────────────────────────────────────────────

    /// <summary>快照是否需要重新加载</summary>
    private bool IsStale
    {
        get
        {
            var loadedAt = Interlocked.Read(ref _loadedAtTicks);
            return loadedAt == 0
                || DateTimeOffset.UtcNow.UtcTicks - loadedAt > SnapshotTimeToLive.Ticks;
        }
    }

    /// <summary>快照过期则重载（对话等异步入口调用；只比 ticks，不查库）</summary>
    public async Task EnsureLoadedAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        if (IsStale)
        {
            await ReloadAsync(db, cancellationToken);
        }
    }

    /// <summary>全量重载：读全部启用的配置、解密 Key、重建条目与命名表（启动时与快照过期后调用）</summary>
    public async Task ReloadAsync(AuthDbContext db, CancellationToken cancellationToken)
    {
        var entities = await db.LlmConfigs.AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);

        var entries = new Dictionary<string, LlmProviderEntry>(StringComparer.Ordinal);
        var names = new Dictionary<(string, string), string>();
        var broken = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var key = (entity.TenantId, entity.Alias);
            var name = UniqueName(ComposeName(key.TenantId, key.Alias), used);
            names[key] = name;

            try
            {
                entries[name] = new LlmProviderEntry(
                    ToProviderOptions(name, entity), TrimPrompt(entity.SystemPrompt), entity.Alias);
            }
            catch (Exception ex)
            {
                // 单条坏了不该让整个目录加载失败：记下来，解析该条时报明确原因
                broken[name] = ex.Message;
                _logger.LogWarning(ex, "模型配置加载失败（tenant={TenantId} alias={Alias}）",
                    entity.TenantId, entity.Alias);
            }
        }

        _broken = broken;
        _entries = entries;
        _names = names;
        _clients.Clear();
        Interlocked.Exchange(ref _loadedAtTicks, DateTimeOffset.UtcNow.UtcTicks);

        _logger.LogInformation("模型目录已加载：{Count} 个 Provider，{Broken} 个配置损坏",
            entries.Count, broken.Count);
    }

    /// <summary>写入配置后直接改内存快照（不回查数据库）</summary>
    /// <remarks>
    /// 为什么不重新加载：框架的工作单元在操作返回之后才提交，此时查库读到的是旧行，
    /// 会把刚写入的配置覆盖回旧值。这里用调用方手上的新值直接建条目，提交结果不影响本次生效。
    /// </remarks>
    public void Apply(LlmProviderConfig entity)
    {
        var key = (entity.TenantId, entity.Alias);
        var name = ProviderName(key.TenantId, key.Alias);

        var entries = new Dictionary<string, LlmProviderEntry>(_entries, StringComparer.Ordinal);
        var names = new Dictionary<(string, string), string>(_names);

        if (!entity.IsActive)
        {
            // 停用的配置视同不存在：留着会让调用方选中后收到上游才暴露的错误
            entries.Remove(name);
            names.Remove(key);
        }
        else
        {
            names[key] = name;
            try
            {
                entries[name] = new LlmProviderEntry(
                    ToProviderOptions(name, entity), TrimPrompt(entity.SystemPrompt), entity.Alias);
                if (_broken.ContainsKey(name))
                {
                    var healed = new Dictionary<string, string>(_broken, StringComparer.Ordinal);
                    healed.Remove(name);
                    _broken = healed;
                }
            }
            catch (Exception ex)
            {
                entries.Remove(name);
                _broken = new Dictionary<string, string>(_broken, StringComparer.Ordinal) { [name] = ex.Message };
                _logger.LogWarning(ex, "模型配置应用失败（tenant={TenantId} alias={Alias}）",
                    entity.TenantId, entity.Alias);
            }
        }

        _entries = entries;
        _names = names;
        _clients.TryRemove(name, out _);
    }

    /// <summary>删除配置后从内存快照摘除</summary>
    public void Remove(string tenantId, string alias)
    {
        var key = (tenantId, alias);
        var name = ProviderName(tenantId, alias);
        if (!_entries.ContainsKey(name) && !_names.ContainsKey(key)) return;

        var entries = new Dictionary<string, LlmProviderEntry>(_entries, StringComparer.Ordinal);
        entries.Remove(name);
        var names = new Dictionary<(string, string), string>(_names);
        names.Remove(key);

        _entries = entries;
        _names = names;
        _clients.TryRemove(name, out _);
    }

    /// <summary>同名冲突时追加 -2、-3 消歧，保证每个配置拿到的名字唯一</summary>
    private static string UniqueName(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;

        for (var i = 2; ; i++)
        {
            var suffix = "-" + i;
            var trimmed = baseName.Length + suffix.Length <= MaxNameLength
                ? baseName
                : baseName[..(MaxNameLength - suffix.Length)];
            var candidate = trimmed + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>纯空白提示词按「没有提示词」处理</summary>
    private static string? TrimPrompt(string? prompt) => string.IsNullOrWhiteSpace(prompt) ? null : prompt;

    /// <summary>把数据库实体转成框架的 Provider 选项（唯一解密明文 Key 的地方）</summary>
    private AIProviderOptions ToProviderOptions(string name, LlmProviderConfig entity)
    {
        var options = new AIProviderOptions(name, MapKind(entity.Provider))
        {
            Endpoint = new Uri(entity.Endpoint),
            ApiKey = _protector.Unprotect(entity.ApiKeyEncrypted, entity.Id),
            ChatModel = entity.Model,
            // 超时统一取本项目的 Ai:TimeoutSeconds（而非框架按 Provider 的 100 秒默认）：
            // 归因仍由框架做（AI_TIMEOUT_001 → 504），但时长只有一个可调点
            Timeout = TimeSpan.FromSeconds(_options.Value.TimeoutSeconds),
        };
        return options;
    }

    /// <summary>本项目存的供应商类型 → 框架的 Kind（框架按 Kind 小写匹配工厂）</summary>
    private static string MapKind(string provider) =>
        provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase)
            ? "openai"
            : provider.ToLowerInvariant();
}