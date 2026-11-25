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

// AI 领域操作：对话 + 可用模型清单。
// 同一文件放同域操作（与 AuthOperations / AdminOperations / LlmConfigOperations 的布局惯例一致），
// 便于一次 review 看到该域的全部契约与错误分类。

#region ai.chat 模型对话（POST /ai:chat）

/// <summary>
/// 模型对话：契约 ID ai.chat → POST /ai:chat
/// </summary>
/// <remarks>
/// 对话本身交给框架的 <see cref="IAIChat"/>（XiHan.Framework.AI 子系统），本操作只负责四件事：
/// 请求校验、每用户配额、提示词解析、计量落库。其余由框架统一处理，不再自己拼客户端与异常分类：
/// - 客户端由 <see cref="DbAIProviderCatalog"/> 按 Kind 解析（凭据仍来自数据库、Key 加密存储）
/// - 超时按 Provider 预算施加并按令牌归因 → AI_TIMEOUT_001（504）
/// - 上游异常翻译成 AI_UNAVAILABLE_001（503），异常原文不进响应（可能回显 Key 片段）
/// - 若登记了 IAIGuardrail，输入与输出两侧都会执行 → AI_DENIED_001
///
/// 提示词为什么走 Messages 而不是框架的 Prompt 目录：框架的提示词来自
/// <c>XiHan:AI:Prompts</c> 配置（启动时读一次、不热更新），而本项目要求提示词存数据库、改完立即生效。
/// 这里按租户从 Prompts 表实时取，组装成 system 消息后交给框架，两个目标都保住。
///
/// 错误码沿用框架 AI 模块的既定语义（不再自造一套）：
/// 404 AI_NOTFOUND_001 配置或 Provider 不存在；400 AI_VALIDATION_001/002 请求或提示词不合法；
/// 504 AI_TIMEOUT_001 模型超时；503 AI_UNAVAILABLE_001 上游不可达或返回错误；
/// 503 AI_UNAVAILABLE_002 配置损坏；503 AI_UNAVAILABLE_003 触发每用户配额（本项目加的唯一一个码）。
/// </remarks>
[Operation(AiChatOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = "authenticated")]
public sealed class AiChatOperation(
    AuthDbContext db,
    IAIChat aiChat,
    DbAIProviderCatalog catalog,
    IOptions<AiChatOptions> aiOptions,
    AiUserRateLimiter rateLimiter,
    AiCallLogger callLogger)
    : IOperation<AiChatRequest, AiChatResponse>
{
    public const string Id = "ai.chat";

    private AiChatOptions Options => aiOptions.Value;

    public async ValueTask<Result<AiChatResponse>> HandleAsync(AiChatRequest request, InvocationContext context)
    {
        // 格式校验放在配额之前：形状不合法的请求不消耗配额，调用方也更快拿到 400
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is < 1 or > 2000)
        {
            return Result.Failure<AiChatResponse>(Error.Validation("AI_VALIDATION_001", "ai.question_empty")
                .WithText("问题不能为空或超过 2000 字符"));
        }

        var tenantId = context.CurrentTenantId();
        var userId = context.GetSecurity().Principal.Id ?? string.Empty;

        // 每用户配额只能放在操作里：管道 Resilience 阶段排在 Authenticate 之前，那时还没有主体，
        // 没法按用户分桶（内核内置限流也只按操作 ID 计数，不区分调用方）
        var decision = rateLimiter.Check(userId, DateTimeOffset.UtcNow);
        if (!decision.Allowed)
        {
            // 被拒绝的调用不写调用日志：否则刷量会顺手把日志表变成新的写入压力来源
            var limited = new Dictionary<string, object?>
            {
                ["id"] = Id,
                ["scope"] = decision.Scope,
                ["retryAfter"] = decision.RetryAfterSeconds,
            };
            return Result.Failure<AiChatResponse>(
                Error.Unavailable("AI_UNAVAILABLE_003", "ai.rate_limited", limited)
                    .WithText(decision.Scope == "day"
                        ? $"当日调用配额已用尽（上限 {Options.UserDailyLimit} 次），请在 {decision.RetryAfterSeconds} 秒后重试"
                        : $"调用过于频繁（每分钟上限 {Options.UserMinuteLimit} 次），请在 {decision.RetryAfterSeconds} 秒后重试"));
        }

        var alias = string.IsNullOrWhiteSpace(request.Alias) ? "default" : request.Alias;
        var promptAlias = string.IsNullOrWhiteSpace(request.PromptAlias) ? null : request.PromptAlias.Trim();
        var started = System.Diagnostics.Stopwatch.StartNew();

        Result<AiChatResponse> result;
        try
        {
            result = await ExecuteAsync(request, context, tenantId, alias, question);
        }
        catch
        {
            // 客户端断开等异常路径：先留下痕迹，再原样抛给框架归因（KERNEL_CANCELLED_001）
            await callLogger.LogAsync(new AiCallRecord(tenantId, userId, alias, promptAlias,
                false, "AI_CANCELLED_001", 0, 0, started.ElapsedMilliseconds));
            throw;
        }

        // 成功与可恢复失败都记一条：失败带错误码，用于统计失败率与排查
        var succeeded = result.IsSuccess;
        var (inputTokens, outputTokens, answer) = result.TryGetValue(out var chat)
            ? (chat.InputTokens, chat.OutputTokens, chat.Answer)
            : (0, 0, (string?)null);

        await callLogger.LogAsync(new AiCallRecord(tenantId, userId, alias, promptAlias,
            succeeded, succeeded ? null : result.Error!.Code,
            inputTokens, outputTokens, started.ElapsedMilliseconds,
            question, answer));

        return result;
    }

    /// <summary>
    /// 解析提示词、组装消息、交给框架 IAIChat。
    /// 与计量、配额分开，是因为那两件事只在 HandleAsync 里做一次，不必在每个返回点重复。
    /// </summary>
    private async ValueTask<Result<AiChatResponse>> ExecuteAsync(
        AiChatRequest request, InvocationContext context, string tenantId, string alias, string question)
    {
        // 快照过期则重载（只比 ticks，不查库）：跨实例或直改数据库的配置变更靠它收敛
        await catalog.EnsureLoadedAsync(db, context.CancellationToken);

        var providerName = catalog.ProviderName(tenantId, alias);

        // 先查目录再交给框架：目录自己的「找不到」报错不知道业务别名（配置根本不存在），
        // 这里知道，能把该录入的别名带进文案（注意 .WithText 只是兜底，有 messageKey 时以语言文件为准，
        // 所以别名要作为消息参数传，模板里写 {alias}）
        if (!catalog.Find(providerName).HasValue)
        {
            return Result.Failure<AiChatResponse>(
                Error.NotFound("AI_NOTFOUND_001", "ai.config_not_found", MessageArguments.Of(("alias", alias)))
                    .WithText($"模型配置不存在或未启用：{alias}，请先通过 llm:upsertconfig 录入"));
        }

        // 提示词解析：显式 promptAlias 优先（实时查库，改完立即生效）；否则用该模型配置的默认提示词
        string? systemPrompt;
        if (!string.IsNullOrWhiteSpace(request.PromptAlias))
        {
            var promptAlias = request.PromptAlias.Trim();
            var prompt = await db.Prompts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Alias == promptAlias && p.IsActive,
                    context.CancellationToken);
            if (prompt is null)
            {
                return Result.Failure<AiChatResponse>(Error.Validation("AI_VALIDATION_002", "ai.prompt_not_found")
                    .WithText($"提示词不存在或未启用：{promptAlias}"));
            }

            systemPrompt = prompt.Content;
        }
        else
        {
            systemPrompt = catalog.SystemPromptOf(providerName);
        }

        // 有提示词时前置 system 消息（人设/边界/输出格式），再接用户问题
        IReadOnlyList<ChatMessage> messages = string.IsNullOrWhiteSpace(systemPrompt)
            ? [new ChatMessage(ChatRole.User, question)]
            : [new ChatMessage(ChatRole.System, systemPrompt), new ChatMessage(ChatRole.User, question)];

        // Provider 名带租户前缀；框架据此解析客户端、施加该 Provider 的超时预算并做错误归因。
        // 超时、上游故障、护栏拦截都由框架翻译成对应的 Error，这里直接透传，不再自己 catch 分类。
        var aiRequest = new FrameworkChatRequest
        {
            Provider = providerName,
            Messages = messages,
        };

        var reply = await aiChat.CompleteAsync(aiRequest, context.CancellationToken);
        if (reply.IsFailure)
        {
            return Result.Failure<AiChatResponse>(reply.Error!);
        }

        if (!reply.TryGetValue(out var response))
        {
            return Result.Failure<AiChatResponse>(
                Error.Unexpected("AI_UNEXPECTED_001", "ai.chat.empty_response"));
        }

        var usage = response.Usage;
        return Result.Success(new AiChatResponse(
            response.Text ?? string.Empty,
            alias,
            (int)(usage?.InputTokenCount ?? 0),
            (int)(usage?.OutputTokenCount ?? 0)));
    }
}

/// <summary>模型对话请求</summary>
/// <param name="Question">问题内容，1-2000 字符</param>
/// <param name="Alias">模型别名，不传用 default</param>
/// <param name="PromptAlias">提示词别名（Prompts 表，租户内）。传了用它作 system 消息；不传用模型配置的默认提示词</param>
public sealed record AiChatRequest(string Question, string? Alias = null, string? PromptAlias = null);

/// <summary>模型对话响应</summary>
/// <param name="Answer">模型回答</param>
/// <param name="Alias">实际使用的模型别名</param>
/// <param name="InputTokens">输入 token 数</param>
/// <param name="OutputTokens">输出 token 数</param>
public sealed record AiChatResponse(string Answer, string Alias, int InputTokens, int OutputTokens);

#endregion

#region ai.models 可用模型清单（GET /ai:models）

/// <summary>
/// 可用模型清单：契约 ID ai.models → GET /ai:models
/// </summary>
/// <remarks>
/// 为什么需要它：<see cref="AiChatRequest.Alias"/> 是开放字符串，但普通登录用户无权调用
/// llm.configs（那是 llm.admin 的地盘），于是调用方只能盲猜 "default"。
/// 本接口按租户列出**已启用**的模型别名，让前端能渲染模型下拉框。
///
/// 最小披露原则：只回别名 / 供应商 / 模型名 / 是否有默认提示词，
/// 不回 Endpoint（内部拓扑）、不回 Key（连掩码都不给——掩码无助于选别名，只是白白增加泄漏面）。
/// 与 llm.configs 的分工：那个是管理员视角的配置管理（含 Endpoint、掩码），这个是普通用户视角的选型清单。
/// </remarks>
[Operation(AiModelsOperation.Id, Expose = Expose.Http, ReadOnly = true,
    Stability = Stability.Experimental, Policy = "authenticated")]
public sealed class AiModelsOperation(AuthDbContext db, IOptions<PagingOptions> paging)
    : IOperation<SessionPagedRequest, SessionPagedResponse<AiModelItem>>
{
    public const string Id = "ai.models";

    public async ValueTask<Result<SessionPagedResponse<AiModelItem>>> HandleAsync(
        SessionPagedRequest request, InvocationContext context)
    {
        var pagingResult = Paging.Validate(request, paging.Value);
        if (!pagingResult.TryGetValue(out var pagingValues))
        {
            return Result.Failure<SessionPagedResponse<AiModelItem>>(pagingResult.Error!);
        }
        var (page, pageSize) = pagingValues;

        var tenantId = context.CurrentTenantId();

        // 只列启用的：停用的配置 ai.chat 也用不上，列出来只会让客户端选中后报 404
        var query = db.LlmConfigs.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.IsActive)
            .AsQueryable();

        // Query 关键字筛选：别名或模型名包含匹配（SQL Server 默认排序规则不区分大小写）
        var keyword = request.Query?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = "%" + Paging.EscapeLike(keyword) + "%";
            query = query.Where(c => EF.Functions.Like(c.Alias, pattern) || EF.Functions.Like(c.Model, pattern));
        }

        var total = await query.LongCountAsync(context.CancellationToken);
        var items = await query
            .OrderBy(c => c.Alias)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            // 投影在数据库侧完成：SystemPrompt 正文不参与传输，只回「有没有」这一位信息
            .Select(c => new AiModelItem(
                c.Alias, c.Provider, c.Model,
                c.SystemPrompt != null && c.SystemPrompt != string.Empty,
                c.UpdatedAt))
            .ToListAsync(context.CancellationToken);

        return Result.Success(new SessionPagedResponse<AiModelItem>(items, total, page, pageSize));
    }
}

/// <summary>可用模型项（普通用户视角，不含 Endpoint 与 Key）</summary>
/// <param name="Alias">模型别名，作为 ai.chat 的 alias 参数使用</param>
/// <param name="Provider">供应商类型</param>
/// <param name="Model">模型名</param>
/// <param name="HasSystemPrompt">是否配置了默认系统提示词（可用 ai.chat 的 promptAlias 覆盖）</param>
/// <param name="UpdatedAt">最近更新时间</param>
public sealed record AiModelItem(string Alias, string Provider, string Model,
    bool HasSystemPrompt, DateTimeOffset UpdatedAt);

#endregion

#region ai.usage 我的用量统计（GET /ai:usage）

/// <summary>
/// 我的模型调用用量：契约 ID ai.usage → GET /ai:usage
/// </summary>
/// <remarks>
/// 数据来自 <see cref="AiCallLog"/>，只统计**当前主体自己的**记录（按 TenantId + UserId 过滤），
/// 管理员想看全租户用量应另做管理接口——把「我的」和「全局」混在一个接口里，
/// 迟早会因为权限判断疏漏导致越权读别人的用量。
///
/// 用途有三个：前端展示「本月已用多少 token」、用户自查是否触顶配额、
/// 运营核对账单与 ai.chat 的实际消耗是否吻合。
///
/// 为什么按天聚合而不是返回原始明细：明细可能有上万行，前端只需要趋势；
/// 需要逐条排查时用调用日志表本身，不必让这个接口承担导出职责。
/// </remarks>
[Operation(AiUsageOperation.Id, Expose = Expose.Http, ReadOnly = true,
    Stability = Stability.Experimental, Policy = "authenticated")]
public sealed class AiUsageOperation(AuthDbContext db)
    : IOperation<AiUsageRequest, AiUsageResponse>
{
    public const string Id = "ai.usage";

    public async ValueTask<Result<AiUsageResponse>> HandleAsync(AiUsageRequest request, InvocationContext context)
    {
        // 窗口上限 90 天：再长就该走运营报表，而不是让一次查询扫半年数据
        if (request.Days is < 1 or > 90)
        {
            return Result.Failure<AiUsageResponse>(Error.Validation("AI_VALIDATION_003", "ai.usage_days_invalid")
                .WithText("统计天数须为 1-90"));
        }

        var tenantId = context.CurrentTenantId();
        var userId = context.GetSecurity().Principal.Id ?? string.Empty;
        var now = DateTimeOffset.UtcNow;
        // 从「Days 天前的 UTC 零点」起算，保证返回的是完整的自然日，不会出现半天
        var since = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-(request.Days - 1)), TimeSpan.Zero);

        var query = db.AiCallLogs.AsNoTracking()
            .Where(l => l.TenantId == tenantId && l.UserId == userId && l.CreatedAt >= since);

        // 一次分组吐回每日聚合，总数在内存里累加——避免「明细拉全表」和「多次往返」两种浪费
        var daily = await query
            .GroupBy(l => l.CreatedAt.Date)
            .Select(g => new
            {
                Date = g.Key,
                Calls = g.LongCount(),
                SuccessCalls = g.Count(l => l.Success),
                InputTokens = g.Sum(l => (long)l.InputTokens),
                OutputTokens = g.Sum(l => (long)l.OutputTokens),
            })
            .OrderBy(x => x.Date)
            .ToListAsync(context.CancellationToken);

        var rows = daily
            .Select(d => new AiUsageDaily(
                d.Date.ToString("yyyy-MM-dd"),
                d.Calls, d.SuccessCalls, d.Calls - d.SuccessCalls,
                d.InputTokens, d.OutputTokens))
            .ToList();

        var totalCalls = rows.Sum(r => r.Calls);
        var inputTokens = rows.Sum(r => r.InputTokens);
        var outputTokens = rows.Sum(r => r.OutputTokens);

        return Result.Success(new AiUsageResponse(
            request.Days,
            totalCalls,
            rows.Sum(r => r.SuccessCalls),
            rows.Sum(r => r.FailedCalls),
            inputTokens,
            outputTokens,
            inputTokens + outputTokens,
            rows));
    }
}

/// <summary>用量查询请求</summary>
/// <param name="Days">统计最近多少天（含今天），1-90，默认 7</param>
public sealed record AiUsageRequest(int Days = 7);

/// <summary>用量汇总</summary>
/// <param name="Days">统计窗口天数（回显）</param>
/// <param name="TotalCalls">总调用次数</param>
/// <param name="SuccessCalls">成功次数</param>
/// <param name="FailedCalls">失败次数</param>
/// <param name="InputTokens">输入 token 合计</param>
/// <param name="OutputTokens">输出 token 合计</param>
/// <param name="TotalTokens">token 总计</param>
/// <param name="Daily">按自然日（UTC）的明细，按日期升序</param>
public sealed record AiUsageResponse(
    int Days,
    long TotalCalls,
    long SuccessCalls,
    long FailedCalls,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    IReadOnlyList<AiUsageDaily> Daily);

/// <summary>单日用量</summary>
/// <param name="Date">日期（yyyy-MM-dd，UTC）</param>
/// <param name="Calls">调用次数</param>
/// <param name="SuccessCalls">成功次数</param>
/// <param name="FailedCalls">失败次数</param>
/// <param name="InputTokens">输入 token</param>
/// <param name="OutputTokens">输出 token</param>
public sealed record AiUsageDaily(
    string Date,
    long Calls,
    long SuccessCalls,
    long FailedCalls,
    long InputTokens,
    long OutputTokens);

#endregion
