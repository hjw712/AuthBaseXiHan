using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>
/// 从调用上下文取租户 ID 的扩展方法。
/// </summary>
/// <remarks>
/// 当前项目 JWT 未带租户 claim，框架 Tenant 阶段解析为空，统一回退到 "default" 租户。
/// 后续接入多租户时只需在 Tenant 阶段填充 SecurityContext.Tenant，本方法无需改动——
/// 所有凭据隔离查询都走这里，不会出现「有的地方取租户、有的地方写死」的漏洞。
/// </remarks>
public static class TenantExtensions
{
    /// <summary>默认租户 ID（单租户阶段的固定值）</summary>
    public const string DefaultTenantId = "default";

    /// <summary>取当前调用的租户 ID，未解析出租户时回退到 default</summary>
    public static string CurrentTenantId(this InvocationContext context)
    {
        var tenantId = context.GetSecurity().Tenant?.Id;
        return string.IsNullOrWhiteSpace(tenantId) ? DefaultTenantId : tenantId;
    }
}
