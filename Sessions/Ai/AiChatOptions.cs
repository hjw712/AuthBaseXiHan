namespace AuthBaseXiHan.Sessions.Ai;

// AI 模块的运行期参数：绑定 appsettings "Ai" 配置节，改配置即改行为。
// 与凭据实体、客户端工厂同目录，因为它们共同构成「怎么调模型」这一层能力；
// Ai:AdminUsers（管理员名单）与 "Ai" 节共用，此处只读与对话行为相关的键。

/// <summary>AI 对话运行参数（绑定 appsettings "Ai" 节）</summary>
/// <remarks>
/// Program.cs 里 Configure&lt;AiChatOptions&gt;(GetSection("Ai")) 绑定；
/// ai.chat 注入 IOptions&lt;AiChatOptions&gt; 读取。字段缺省值即代码内默认，配置可不写这些键。
/// </remarks>
public sealed class AiChatOptions
{
    /// <summary>配置节名（与管理员名单共用 "Ai" 节）</summary>
    public const string SectionName = "Ai";

    /// <summary>
    /// 单次模型调用的超时秒数（默认 60）。
    /// 上游模型挂起时若无上限，请求会一直占用连接并持续计费，故必须有硬超时。
    /// 取值须小于反向代理/IIS 的请求超时，否则代理先断开、超时错误无法回传。
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// 每用户每分钟调用上限（默认 20；设为 0 或负数表示不限制）。
    /// 分钟配额挡住突发刷量；按用户而非全局，避免一个用户把整个租户的额度吃光。
    /// </summary>
    public int UserMinuteLimit { get; set; } = 20;

    /// <summary>
    /// 每用户每自然日（UTC）调用上限（默认 1000；设为 0 或负数表示不限制）。
    /// 日配额兜住「细水长流」式的调用：每分钟不超限但整天累计仍可能烧掉大量额度。
    /// </summary>
    public int UserDailyLimit { get; set; } = 1000;

    /// <summary>
    /// 是否把问题与回答正文写进调用日志（默认 false）。
    /// 默认关闭：正文属于用户内容，落库即扩大泄露面与合规负担，而计费只需要 token 与次数。
    /// 排查具体回答质量的问题时才临时打开，写入的仍是截断后的片段（问题 500 字符、回答 2000 字符）。
    /// </summary>
    public bool LogContent { get; set; }
}
