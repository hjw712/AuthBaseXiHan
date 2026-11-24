using AuthBaseXiHan.Sessions.Captcha;
using AuthBaseXiHan.Sessions.Jwt;
using AuthBaseXiHan.Sessions.Session;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Operations;

// 通用会话契约与实现（auth / admin 双轨共用，只写一遍）：
// - 图形验证码：双轨登录共用的第一道人机校验（auth.login 与 admin.login 用同一验证码服务）
// - 身份查询 / 登出 / 强制下线：单接口双轨通用（common:whoami / common:logout / common:logout-all），
//   会话轨道直接从 JWT 的 token_kind claim 推导，与签发端（CredentialAuthenticator → JwtTokenIssuer）
//   同一事实来源，不需要也不该按轨道拆两套路由；策略挂 authenticated（任何登录主体可用），
//   实际的会话有效性由会话门禁（SessionGateHandler）把关。
// 登录不在本文件：契约与实现各轨自持——AuthOperations.LoginOperation（auth.login）、
// AdminOperations.AdminLoginOperation（admin.login），各自持有请求/响应契约与完整流程，互不引用。

#region 图形验证码
/// <summary>
/// 图形验证码操作：双轨登录共用的第一道人机校验（auth.login 与 admin.login 用同一验证码服务），
/// SVG 以 data URI 返回（前端可直接 &lt;img src&gt;）。
/// 契约 ID common.captcha → GET /common:captcha（whoami 同款非标准动词，ReadOnly 标记只读可重复调用）
/// </summary>
[Operation(CaptchaOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    ReadOnly = true)]
public sealed class CaptchaOperation(CaptchaStore captchas) : IOperation<CaptchaRequest, CaptchaResponse>
{
    public const string Id = "common.captcha";

    public async ValueTask<Result<CaptchaResponse>> HandleAsync(CaptchaRequest request, InvocationContext context)
    {
        var (id, image) = await captchas.CreateAsync(context.CancellationToken);
        return Result.Success(new CaptchaResponse(id, image, CaptchaStore.ExpiresIn));
    }
}

/// <summary>验证码请求（空载荷）</summary>
public sealed record CaptchaRequest;

/// <summary>验证码响应</summary>
/// <param name="CaptchaId">验证码 ID，登录时随 captchaCode 一起提交（auth / admin 登录通用）</param>
/// <param name="Image">SVG 图像的 data URI，前端可直接作为 &lt;img src&gt; 展示</param>
/// <param name="ExpiresIn">有效期秒数（2 分钟），过期或尝试一次后作废</param>
public sealed record CaptchaResponse(string CaptchaId, string Image, int ExpiresIn);
#endregion

#region 通用身份查询
/// <summary>双轨通用身份查询请求（空载荷）</summary>
public sealed record SessionWhoAmIRequest;

/// <summary>身份查询响应：当前调用方身份与角色，前端可据此显隐入口</summary>
/// <param name="IsAuthenticated">是否已认证</param>
/// <param name="Id">主体 ID（用户 ID 或管理员 ID），未认证为 null</param>
/// <param name="Scheme">认证方案</param>
/// <param name="DisplayName">显示名（账号）</param>
/// <param name="Roles">角色列表（管理员：admin 恒有，超管另有 admin.super 与 llm.admin；普通用户：空或配置注入的 llm.admin）</param>
public sealed record SessionWhoAmIResponse(bool IsAuthenticated, string? Id, string? Scheme, string? DisplayName,
    IReadOnlyList<string> Roles);

/// <summary>
/// 身份查询操作（单接口双轨通用）：读 principal 投影身份与角色，用户令牌与管理员令牌都可用。
/// 路由区分交给响应里的 roles / token_kind 语义，不按轨道拆两份只读接口。
/// 契约 ID common.whoami → GET /common:whoami（whoami 非标准动词，ReadOnly 标记只读可重复调用）
/// </summary>
[Operation(WhoAmIOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    ReadOnly = true, Policy = "authenticated")]
public sealed class WhoAmIOperation : IOperation<SessionWhoAmIRequest, SessionWhoAmIResponse>
{
    public const string Id = "common.whoami";

    public ValueTask<Result<SessionWhoAmIResponse>> HandleAsync(SessionWhoAmIRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;

        return ValueTask.FromResult(Result.Success(new SessionWhoAmIResponse(
            principal.IsAuthenticated,
            principal.IsAuthenticated ? principal.Id : null,
            principal.AuthenticationScheme,
            principal.DisplayName,
            [.. principal.ClaimValues("role")])));
    }
}
#endregion

#region 通用登出
/// <summary>双轨通用登出请求（空载荷，令牌从 Authorization 头取）</summary>
public sealed record SessionLogoutRequest;

/// <summary>登出响应</summary>
/// <param name="Revoked">会话是否已吊销（当前令牌此后立即失效）</param>
public sealed record SessionLogoutResponse(bool Revoked);

/// <summary>
/// 登出操作（单接口双轨通用）：吊销当前令牌的会话，令牌验签仍通过但门禁查不到会话即失效。
/// 会话轨道从 token_kind claim 推导（用户令牌吊 user 轨、管理员令牌吊 admin 轨），无差异点。
/// 契约 ID common.logout → POST /common:logout（请求体传 {}）
/// </summary>
[Operation(LogoutOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    Policy = "authenticated")]
public sealed class LogoutOperation(SessionStore sessions)
    : IOperation<SessionLogoutRequest, SessionLogoutResponse>
{
    public const string Id = "common.logout";

    public async ValueTask<Result<SessionLogoutResponse>> HandleAsync(
        SessionLogoutRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;
        var jti = principal.ClaimValues("jti").FirstOrDefault();

        // 走到这里说明会话门禁已放行，jti 必然存在；防御式兜底
        if (jti is null || principal.Id is null)
        {
            return Result.Failure<SessionLogoutResponse>(SessionGateHandler.SessionInvalid());
        }

        await sessions.RevokeAsync(SessionScopes.FromTokenKind(principal), jti, principal.Id, context.CancellationToken);
        return Result.Success(new SessionLogoutResponse(true));
    }
}

/// <summary>会话轨道推导助手：从 JWT 的 token_kind claim 读取会话类型</summary>
internal static class SessionScopes
{
    /// <summary>token_kind=admin → Admin 轨道，否则 User 轨道（与 AdminPolicy 门禁同一判定来源）</summary>
    internal static SessionScope FromTokenKind(Principal principal) =>
        principal.ClaimValues("token_kind").Contains(JwtTokenIssuer.AdminKind, StringComparer.OrdinalIgnoreCase)
            ? SessionScope.Admin
            : SessionScope.User;
}
#endregion

#region 通用强制下线
/// <summary>双轨通用强制下线请求（空载荷，吊销当前账号在该轨道的全部会话）</summary>
public sealed record SessionLogoutAllRequest;

/// <summary>强制下线响应</summary>
/// <param name="Revoked">吊销的会话数（含发起本次调用的设备）</param>
public sealed record SessionLogoutAllResponse(int Revoked);

/// <summary>
/// 强制下线操作（单接口双轨通用）：吊销当前账号在其所属轨道的所有设备会话（改密码 / 疑似被盗时用）。
/// 会话轨道从 token_kind claim 推导；Redis 索引键按轨道命名空间隔离，互不误伤。
/// 契约 ID common.logout-all → POST /common:logout-all（请求体传 {}，含发起本次调用的设备）
/// </summary>
[Operation(LogoutAllOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    Policy = "authenticated")]
public sealed class LogoutAllOperation(SessionStore sessions)
    : IOperation<SessionLogoutAllRequest, SessionLogoutAllResponse>
{
    public const string Id = "common.logout-all";

    public async ValueTask<Result<SessionLogoutAllResponse>> HandleAsync(
        SessionLogoutAllRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;

        if (principal.Id is null)
        {
            return Result.Failure<SessionLogoutAllResponse>(SessionGateHandler.SessionInvalid());
        }

        var revoked = await sessions.RevokeAllAsync(SessionScopes.FromTokenKind(principal), principal.Id, context.CancellationToken);
        return Result.Success(new SessionLogoutAllResponse(revoked));
    }
}
#endregion
