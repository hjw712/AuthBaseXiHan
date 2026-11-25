using XiHan.Framework.Domain.Entities;
using XiHan.Framework.Persistence.Abstractions;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 模型调用日志：每次 ai.chat 落一行，用于成本核算、容量规划与排障。
/// </summary>
/// <remarks>
/// 为什么单独一张表而不是塞进业务表：token 是**计费**计量，需要按人/按天/按别名聚合，
/// 与业务实体的一对多关系无关；独立成表后统计查询不会拖累业务表，也方便以后按月归档或分区。
///
/// 默认**不记录问题与回答正文**（<c>Ai:LogContent</c> 开关控制，缺省关闭）：
/// 正文属于用户内容，落库即扩大泄露面与合规负担，而成本核算只需要 token 与次数。
/// 确实需要排障时把开关打开，仍只存截断后的片段。
///
/// 索引按「谁在什么时候用了多少」设计：(TenantId, UserId, CreatedAt) 覆盖
/// ai.usage 的全部查询；(TenantId, CreatedAt) 覆盖运营侧的全局用量统计。
/// </remarks>
[EntityIndex(nameof(TenantId), nameof(UserId), nameof(CreatedAt), Name = "IX_AiCallLogs_Tenant_User_Time")]
[EntityIndex(nameof(TenantId), nameof(CreatedAt), Name = "IX_AiCallLogs_Tenant_Time")]
public sealed class AiCallLog : IEntity<long>
{
    /// <summary>主键：整数主键按 EF Core 约定由数据库自增生成</summary>
    public long Id { get; set; }

    /// <summary>租户 ID（用量按租户隔离，与模型配置同一维度）</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>调用主体 ID（登录用户或管理员的 ID；未认证调用为空串）</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>实际使用的模型别名（成本归因到别名，具体模型名可从配置表按别名关联）</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>提示词别名（请求显式指定才有值）</summary>
    public string? PromptAlias { get; set; }

    /// <summary>是否成功（失败也记录，用于统计失败率与排查）</summary>
    public bool Success { get; set; }

    /// <summary>失败时的错误码（如 AI_TIMEOUT_001），成功时为 null</summary>
    public string? ErrorCode { get; set; }

    /// <summary>输入 token 数（模型未回报时为 0）</summary>
    public int InputTokens { get; set; }

    /// <summary>输出 token 数（模型未回报时为 0）</summary>
    public int OutputTokens { get; set; }

    /// <summary>端到端耗时（毫秒），含排队与上游调用</summary>
    public long ElapsedMs { get; set; }

    /// <summary>问题正文（仅 Ai:LogContent=true 时写入，截断到 500 字符）</summary>
    public string? Question { get; set; }

    /// <summary>回答正文（仅 Ai:LogContent=true 时写入，截断到 2000 字符）</summary>
    public string? Answer { get; set; }

    /// <summary>发生时间（UTC，用量按日聚合的基准）</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}