using AuthBaseXiHan.Sessions.Jwt;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Sessions.Admin;

/// <summary>
/// 管理员门禁策略：仅允许持有 admin 角色且为「管理员会话」的主体进入管理接口。
/// </summary>
/// <remarks>
/// 实现框架原生 <see cref="IPolicy"/>，容器注册后 PolicyCatalog 按 Name 自动发现。双重校验：
/// ① token_kind claim 必须是 admin（管理员登录签发，普通用户令牌哪怕伪造出 admin 角色也过不了）；
/// ② 角色声明含 admin。策略名写进契约的 Policy 属性，启动时框架核对未注册即终止启动——
/// 宁可失败也不能「缺策略被当放行」。
/// </remarks>
public sealed class AdminPolicy : IPolicy
{
    /// <summary>策略名，与 [Operation(Policy = ...)] 中一致</summary>
    public const string PolicyName = "admin";

    /// <inheritdoc />
    public string Name => PolicyName;

    /// <inheritdoc />
    public ValueTask<Result<AuthorizationResult>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var principal = request.Security.Principal;
        if (principal is null || !principal.IsAuthenticated)
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_001", "admin.auth_required").WithText("请先登录"))));
        }

        // 会话类型必须是管理员轨道（用户令牌在这里被拒，不进角色判定）
        var kind = principal.ClaimValues(JwtTokenIssuer.TokenKindClaim).FirstOrDefault();
        if (!string.Equals(kind, JwtTokenIssuer.AdminKind, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_002", "admin.role_required")
                    .WithText("需要管理员会话（请用管理员账号登录获取的令牌）"))));
        }

        var roles = principal.ClaimValues("role");
        if (!roles.Contains(PolicyName, StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_002", "admin.role_required").WithText("需要管理员权限"))));
        }

        return ValueTask.FromResult(Result.Success(AuthorizationResult.Allow()));
    }
}

/// <summary>
/// 超级管理员策略：开通/重置/停用/删除管理账号等管理操作仅限 admin.super 角色且为管理员会话。
/// </summary>
public sealed class AdminSuperPolicy : IPolicy
{
    /// <summary>策略名，与 [Operation(Policy = ...)] 中一致</summary>
    public const string PolicyName = "admin.super";

    /// <inheritdoc />
    public string Name => PolicyName;

    /// <inheritdoc />
    public ValueTask<Result<AuthorizationResult>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var principal = request.Security.Principal;
        if (principal is null || !principal.IsAuthenticated)
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_001", "admin.auth_required").WithText("请先登录"))));
        }

        // 会话类型必须是管理员轨道（与 AdminPolicy 同款双重校验）
        var kind = principal.ClaimValues(JwtTokenIssuer.TokenKindClaim).FirstOrDefault();
        if (!string.Equals(kind, JwtTokenIssuer.AdminKind, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_003", "admin.super_required")
                    .WithText("需要超级管理员权限"))));
        }

        var roles = principal.ClaimValues("role");
        if (!roles.Contains(PolicyName, StringComparer.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(Result.Success(AuthorizationResult.Deny(
                Error.Denied("ADMIN_DENIED_003", "admin.super_required").WithText("需要超级管理员权限"))));
        }

        return ValueTask.FromResult(Result.Success(AuthorizationResult.Allow()));
    }
}
