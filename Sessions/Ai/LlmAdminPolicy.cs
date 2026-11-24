using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>
/// 模型配置管理策略：仅允许持有 llm.admin 角色的主体改 URL / Key。
/// </summary>
/// <remarks>
/// 实现框架原生 <see cref="IPolicy"/>，由容器注册后 PolicyCatalog 按 Name 自动发现。
/// 判定规则：已认证 + 角色声明含 llm.admin；角色信息从 JWT claim 读取（后续可换成 RBAC 存储）。
/// 策略名写进契约的 Policy 属性，启动时框架会核对策略是否已注册，未注册直接终止启动——
/// 宁可启动失败，也不能出现「策略不存在被当放行」的安全事故。
/// </remarks>
public sealed class LlmAdminPolicy : IPolicy
{
    /// <summary>策略名，与 [Operation(Policy = ...)] 中一致</summary>
    public const string PolicyName = "llm.admin";

    /// <inheritdoc />
    public string Name => PolicyName;

    /// <inheritdoc />
    public ValueTask<Result<AuthorizationResult>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var principal = request.Security.Principal;
        if (principal is null || !principal.IsAuthenticated)
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("LLM_DENIED_001", "llm.auth_required").WithText("请先登录"))));
        }

        var roles = principal.ClaimValues("role");
        if (!roles.Contains(PolicyName, StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("LLM_DENIED_002", "llm.admin_required").WithText("需要模型管理权限"))));
        }

        return ValueTask.FromResult(Result.Success(AuthorizationResult.Allow()));
    }
}
