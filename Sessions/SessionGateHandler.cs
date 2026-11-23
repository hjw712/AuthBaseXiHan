using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;
using XiHan.Framework.Security.Jwt;
using XiHan.Framework.Security.Abstractions;
using XiHan.Framework.Security.Abstractions.Authentication;

namespace AuthBaseXiHan.Sessions;

/// <summary>
/// 会话门禁：JWT + Redis 双轨的校验侧
/// </summary>
/// <remarks>
/// 装饰框架的 <see cref="JwtAuthenticationHandler"/>：先让它做验签（无状态轨），验签通过后查 Redis 会话（吊销轨），
/// 会话不存在（已登出 / 已被强制下线 / Redis 中过期）即拒绝认证。内核 Authenticate 阶段按
/// 方案名取「第一个」登记的处理器，本装饰器先于 AddXiHan 登记（DI 顺序），框架原始处理器
/// 的具体实例由 Program.cs 显式注册后注入成为内层。
///
/// 拒绝用框架的凭据无效码（SecurityErrors.InvalidCredential，SECURITY_DENIED_003）：
/// ProblemResponse 只对 SECURITY_DENIED_001/003 给 401 + WWW-Authenticate（RFC 6750 的
/// invalid_token 语义），吊销的令牌就该按「凭据失效」处理，客户端收到 401 触发重新登录。
/// </remarks>
public sealed class SessionGateHandler(JwtAuthenticationHandler inner, SessionStore sessions)
    : IAuthenticationHandler
{
    /// <summary>方案名透传内层（Bearer），内核按它选处理器</summary>
    public string Scheme => inner.Scheme;

    public async ValueTask<Result<Principal>> AuthenticateAsync(
        AuthenticationCredential credential,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        // 第一轨：JWT 验签（签名 / 过期 / 签发者），失败原样返回框架的错误
        var result = await inner.AuthenticateAsync(credential, context, cancellationToken);
        if (result.IsFailure || !result.TryGetValue(out var principal))
        {
            return result;
        }

        // 第二轨：Redis 会话必须存在。jti 缺失说明令牌不是本服务签发的新版（如历史令牌），同样拒绝
        var jti = principal.ClaimValues("jti").FirstOrDefault();
        if (jti is null || principal.Id is null || !await sessions.ExistsAsync(jti, principal.Id, context.CancellationToken))
        {
            return Result.Failure<Principal>(SessionInvalid());
        }

        return result;
    }

    /// <summary>会话失效错误：code 与框架凭据无效一致，走 401 + WWW-Authenticate（登出操作也用它兜底）</summary>
    internal static Error SessionInvalid() =>
        Error.Denied(SecurityErrors.InvalidCredential, "auth.session_invalid")
            .WithText("会话已失效或已注销，请重新登录");
}
