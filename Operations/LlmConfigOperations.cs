using System.Text.RegularExpressions;
using AuthBaseXiHan.Sessions.Ai;
using AuthBaseXiHan.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

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
public sealed class LlmConfigUpsertOperation(AuthDbContext db, IChatClientFactory factory)
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
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_002", "llm.endpoint_invalid")
                .WithText("接口地址须为 HTTPS（本机开发允许 http://localhost）"));
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

        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(request.ApiKey))
            {
                return Result.Failure<LlmConfigUpsertResponse>(Error.Validation("LLM_VALIDATION_004", "llm.key_required")
                    .WithText("新增配置时 API Key 不能为空"));
            }

            db.LlmConfigs.Add(new LlmProviderConfig
            {
                TenantId = tenantId,
                Alias = request.Alias,
                Provider = request.Provider,
                Endpoint = request.Endpoint,
                ApiKeyEncrypted = factory.ProtectApiKey(request.ApiKey),
                Model = request.Model,
                SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt) ? null : request.SystemPrompt,
                IsActive = request.IsActive,
                CreatedAt = now,
                UpdatedAt = now,
            });
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
                existing.ApiKeyEncrypted = factory.ProtectApiKey(request.ApiKey);
            }
        }

        // 写库由框架工作单元统一提交；这里先清缓存，避免旧客户端继续被使用
        await factory.InvalidateAsync(request.Alias, tenantId, context.CancellationToken);

        return Result.Success(new LlmConfigUpsertResponse(existing?.Id ?? 0, request.Alias, true));
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
public sealed class LlmConfigTestOperation(IChatClientFactory factory)
    : IOperation<LlmConfigTestRequest, LlmConfigTestResponse>
{
    public const string Id = "llm.testconfig";

    public async ValueTask<Result<LlmConfigTestResponse>> HandleAsync(
        LlmConfigTestRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();
        IChatClient client;
        try
        {
            client = await factory.CreateAsync(request.Alias, tenantId, context.CancellationToken);
        }
        catch (Exception ex)
        {
            return Result.Failure<LlmConfigTestResponse>(Error.Validation("LLM_VALIDATION_005", "llm.test_failed")
                .WithText($"配置加载失败：{ex.Message}"));
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var reply = await client.GetResponseAsync("ping", cancellationToken: context.CancellationToken);
            sw.Stop();
            return Result.Success(new LlmConfigTestResponse(true, sw.ElapsedMilliseconds,
                Truncate(reply.Text ?? string.Empty, 100), null));
        }
        catch (Exception ex)
        {
            sw.Stop();
            // 上游异常信息可能含敏感内容，只回类型与消息首行
            var message = ex.Message.Split('\n')[0];
            return Result.Success(new LlmConfigTestResponse(false, sw.ElapsedMilliseconds, null, message));
        }
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
public sealed class LlmConfigDeleteOperation(AuthDbContext db, IChatClientFactory factory)
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
        await factory.InvalidateAsync(entity.Alias, tenantId, context.CancellationToken);
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
