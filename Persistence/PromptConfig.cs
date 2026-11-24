using AuthBaseXiHan.Sessions.Ai;
using XiHan.Framework.Domain.Entities;
using XiHan.Framework.Persistence.Abstractions;

namespace AuthBaseXiHan.Persistence;

/// <summary>
/// 提示词实体（纯 POCO，不依赖 EF Core）。独立于模型配置（<see cref="LlmProviderConfig"/>）：
/// 提示词与模型解耦——同一个模型可按调用参数换用不同提示词，同一提示词也可给多个模型复用。
/// 模型配置上的 SystemPrompt 退化为「该模型的默认提示词」，调用方未指定提示词别名时才用它。
/// 实现框架的 <see cref="IEntity{TKey}"/> 协议才能登记仓储；
/// 唯一索引由 [EntityIndex] 声明，Provider 在建模时映射为 HasIndex —— 换 Provider 不用改这里。
/// </summary>
[EntityIndex(nameof(TenantId), nameof(Alias), IsUnique = true, Name = "UX_Prompts_Tenant_Alias")]
public sealed class PromptConfig : IEntity<long>
{
    /// <summary>主键：整数主键按 EF Core 约定由数据库自增生成</summary>
    public long Id { get; set; }

    /// <summary>租户 ID（对话按租户取提示词，与模型配置同一隔离维度）</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>提示词别名，租户内唯一；ai.chat 用 promptAlias 引用</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>提示词正文（system 消息），≤4000 字符</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>是否启用：停用后不能被对话引用</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>创建时间（UTC）</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>最近更新时间（UTC）</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
