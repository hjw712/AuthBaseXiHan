using System.Text.RegularExpressions;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions.Ai;
using AuthBaseXiHan.Sessions.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Persistence.Abstractions.Repositories;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Operations;

// 提示词管理：独立 Prompts 表，与模型配置解耦——
// 同一个模型可按调用参数（ai.chat 的 promptAlias）换用不同提示词，同一提示词可给多个模型复用。
// 模型配置上的 SystemPrompt 退化为「默认提示词」，调用方未指定时才用。
// 全部需要 llm.admin 策略（超级管理员自带该角色）；提示词每次对话实时查库，无缓存，改完立即生效。

#region 提示词列表
/// <summary>
/// 提示词列表：契约 ID prompt.configs → GET /prompt:configs（分页 + 关键字筛选）
/// 请求/响应复用通用分页契约（Operations/Paging.cs）：SessionPagedRequest / SessionPagedResponse&lt;PromptItem&gt;，
/// 分页参数由 Paging.Validate 按 "Paging" 配置节校验（DefaultPageSize / MaxPageSize / MaxPage 全局可配）。
/// Query 筛选：对别名做包含匹配（忽略大小写，通配符转义按字面处理）。
/// 注意不用 .list 后缀：内核 HTTP 适配器把 .list 特判为 GET 集合根路径（GET /prompt），
/// 与 llm.configs 风格保持一致用 configs
/// </summary>
[Operation(PromptListOperation.Id, Expose = Expose.Http, ReadOnly = true,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class PromptListOperation(AuthDbContext db, IOptions<PagingOptions> paging)
    : IOperation<SessionPagedRequest, SessionPagedResponse<PromptItem>>
{
    public const string Id = "prompt.configs";

    public async ValueTask<Result<SessionPagedResponse<PromptItem>>> HandleAsync(
        SessionPagedRequest request, InvocationContext context)
    {
        // 分页参数校验与规范化（默认页大小/上限来自 Paging 配置）
        var pagingResult = Paging.Validate(request, paging.Value);
        if (!pagingResult.TryGetValue(out var pagingValues))
        {
            // 失败分支里 Error 必有值；TryGetValue 成功时才解构
            return Result.Failure<SessionPagedResponse<PromptItem>>(pagingResult.Error!);
        }
        var (page, pageSize) = pagingValues;

        var tenantId = context.CurrentTenantId();
        var query = db.Prompts.AsNoTracking().Where(p => p.TenantId == tenantId);

        // Query 关键字筛选：别名包含匹配（SQL Server 默认排序规则不区分大小写）
        var keyword = request.Query?.Trim();
        if (!string.IsNullOrEmpty(keyword))
        {
            var pattern = "%" + Paging.EscapeLike(keyword) + "%";
            query = query.Where(p => EF.Functions.Like(p.Alias, pattern));
        }

        // 总数单独查（筛选后、不带排序与投影），items 再 Skip/Take——前端需要 total 渲染页码
        var total = await query.LongCountAsync(context.CancellationToken);
        var items = await query
            .OrderBy(p => p.Alias)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new PromptItem(p.Id, p.Alias, p.Content, p.IsActive, p.CreatedAt, p.UpdatedAt))
            .ToListAsync(context.CancellationToken);

        return Result.Success(new SessionPagedResponse<PromptItem>(items, total, page, pageSize));
    }
}

/// <summary>提示词项</summary>
/// <param name="PromptId">提示词 ID</param>
/// <param name="Alias">别名（ai.chat 的 promptAlias 用它引用）</param>
/// <param name="Content">提示词正文（system 消息）</param>
/// <param name="IsActive">是否启用</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">最近更新时间</param>
public sealed record PromptItem(long PromptId, string Alias, string Content, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
#endregion

#region 新增或更新
/// <summary>
/// 新增或更新提示词：契约 ID prompt.upsert → POST /prompt:upsert
/// 按 (租户, 别名) upsert；内容实时查库生效，无缓存无需清缓存
/// </summary>
[Operation(PromptUpsertOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class PromptUpsertOperation(AuthDbContext db, IRepository<PromptConfig, long> prompts)
    : IOperation<PromptUpsertRequest, PromptUpsertResponse>
{
    public const string Id = "prompt.upsert";

    public async ValueTask<Result<PromptUpsertResponse>> HandleAsync(
        PromptUpsertRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();
        var alias = request.Alias.Trim();

        // 字段校验：别名格式与模型配置同规则；内容上限与库列长度一致
        if (!Regex.IsMatch(alias, @"^[a-zA-Z0-9_-]{1,50}$"))
        {
            return Result.Failure<PromptUpsertResponse>(Error.Validation("PROMPT_VALIDATION_001", "prompt.alias_invalid")
                .WithText("别名须为 1-50 位字母、数字、下划线或中划线"));
        }
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 4000)
        {
            return Result.Failure<PromptUpsertResponse>(Error.Validation("PROMPT_VALIDATION_002", "prompt.content_invalid")
                .WithText("提示词内容不能为空且不超过 4000 字符"));
        }

        var existing = await db.Prompts
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Alias == alias, context.CancellationToken);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            // 仓储写入在事务阶段的工作单元内执行，唯一索引冲突以失败结果返回
            var added = await prompts.AddAsync(new PromptConfig
            {
                TenantId = tenantId,
                Alias = alias,
                Content = request.Content,
                IsActive = request.IsActive,
                CreatedAt = now,
                UpdatedAt = now,
            }, context.CancellationToken);
            if (added.IsFailure)
            {
                return Result.Failure<PromptUpsertResponse>(Error.Conflict("PROMPT_CONFLICT_001", "prompt.alias_exists")
                    .WithText("别名已存在"));
            }
        }
        else
        {
            existing.Content = request.Content;
            existing.IsActive = request.IsActive;
            existing.UpdatedAt = now;
        }

        return Result.Success(new PromptUpsertResponse(existing?.Id ?? 0, alias, true));
    }
}

/// <summary>新增或更新提示词请求</summary>
/// <param name="Alias">提示词别名，1-50 位字母数字下划线中划线（ai.chat 用它引用）</param>
/// <param name="Content">提示词正文（system 消息），1-4000 字符</param>
/// <param name="IsActive">是否启用，默认 true</param>
public sealed record PromptUpsertRequest(string Alias, string Content, bool IsActive = true);

/// <summary>新增或更新提示词响应</summary>
/// <param name="PromptId">提示词 ID（新增时框架提交后才生成，此处为占位）</param>
/// <param name="Alias">提示词别名</param>
/// <param name="Updated">是否已更新</param>
public sealed record PromptUpsertResponse(long PromptId, string Alias, bool Updated);
#endregion

#region 删除
/// <summary>
/// 删除提示词：契约 ID prompt.deleteprompt → POST /prompt:deleteprompt
/// 注意不用 .delete 后缀：内核 HTTP 适配器把 .delete 特判为 DELETE 动词（DELETE /prompt），与项目全 POST 风格不符
/// </summary>
[Operation(PromptDeleteOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = LlmAdminPolicy.PolicyName)]
public sealed class PromptDeleteOperation(AuthDbContext db) : IOperation<PromptDeleteRequest, PromptDeleteResponse>
{
    public const string Id = "prompt.deleteprompt";

    public async ValueTask<Result<PromptDeleteResponse>> HandleAsync(
        PromptDeleteRequest request, InvocationContext context)
    {
        var tenantId = context.CurrentTenantId();
        var entity = await db.Prompts.FirstOrDefaultAsync(
            p => p.Id == request.PromptId && p.TenantId == tenantId, context.CancellationToken);

        if (entity is null)
        {
            return Result.Failure<PromptDeleteResponse>(
                Error.NotFound("PROMPT_NOTFOUND_001", "prompt.not_found").WithText("提示词不存在"));
        }

        db.Prompts.Remove(entity);
        return Result.Success(new PromptDeleteResponse(true));
    }
}

/// <summary>删除提示词请求</summary>
/// <param name="PromptId">提示词 ID</param>
public sealed record PromptDeleteRequest(long PromptId);

/// <summary>删除提示词响应</summary>
/// <param name="Deleted">是否已删除</param>
public sealed record PromptDeleteResponse(bool Deleted);
#endregion
