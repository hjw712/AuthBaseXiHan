namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>
/// 模型凭据配置实体：LLM / Agent 的 Endpoint + ApiKey + Model 存数据库，替代 appsettings 硬编码。
/// </summary>
/// <remarks>
/// 关键设计：
/// - ApiKeyEncrypted 存 Data Protection 加密后的密文（varbinary），明文永不入库、不进日志、不回显前端；
/// - (TenantId, Alias) 唯一索引：不同租户可以有同名 Alias（如 default），互不干扰；
/// - 业务代码只认 Alias，换供应商只改这一行记录，不改代码、不重启。
/// </remarks>
public sealed class LlmProviderConfig
{
    /// <summary>主键</summary>
    public long Id { get; set; }

    /// <summary>租户 ID（字符串，兼容 Guid / 数字租户方案）</summary>
    public string TenantId { get; set; } = "default";

    /// <summary>业务别名，如 default / kb-chat；业务只认这个</summary>
    public string Alias { get; set; } = "default";

    /// <summary>供应商类型：OpenAICompatible / AzureOpenAI / DashScope</summary>
    public string Provider { get; set; } = "OpenAICompatible";

    /// <summary>接口地址，如 https://api.deepseek.com/v1</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>加密后的 API Key 密文</summary>
    public byte[] ApiKeyEncrypted { get; set; } = [];

    /// <summary>模型名，如 deepseek-chat</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>系统提示词（可空）：对话时作为 system 消息前置，定义人设/边界/输出格式；null 或空串表示不使用</summary>
    [System.ComponentModel.DataAnnotations.MaxLength(4000)]
    public string? SystemPrompt { get; set; }

    /// <summary>是否启用</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>更新时间</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
