using System.Text.RegularExpressions;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions;
using Microsoft.EntityFrameworkCore;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Persistence.Abstractions.Repositories;
using XiHan.Framework.Primitives;
using XiHan.Framework.Security.Abstractions;

namespace AuthBaseXiHan.Operations;

// 契约 record 的 XML 注释会被 Kernel.Generators 折进 OpenAPI Schema 的 description，展示在接口文档里
// 每个操作连同它的请求/响应契约就近放在同一个 #region 里

#region 注册
/// <summary>
/// 注册操作：契约 ID auth.register → HTTP 适配器自动映射 POST /auth:register
/// </summary>
[Operation(RegisterOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http)]
public sealed class RegisterOperation(AuthDbContext db, IRepository<User, long> users)
    : IOperation<RegisterRequest, RegisterResponse>
{
    public const string Id = "auth.register";

    public async ValueTask<Result<RegisterResponse>> HandleAsync(RegisterRequest request, InvocationContext context)
    {
        var cancellationToken = context.CancellationToken;
        var userName = request.UserName.Trim();

        // 字段格式校验（JSON Schema 只能约束必填，格式规则在此显式校验）
        // 错误码格式约定：<域名>_<种类>_<编号>；messageKey 为小写点分本地化键，中文文本用 WithText 附加
        if (!Regex.IsMatch(userName, @"^[a-zA-Z0-9_]{4,20}$"))
        {
            return Result.Failure<RegisterResponse>(
                Error.Validation("AUTH_VALIDATION_001", "auth.username_invalid")
                    .WithText("账号须为 4-20 位字母、数字或下划线"));
        }
        if (!Regex.IsMatch(request.Password, @"^(?=.*[A-Za-z])(?=.*\d).{8,32}$"))
        {
            return Result.Failure<RegisterResponse>(
                Error.Validation("AUTH_VALIDATION_002", "auth.password_invalid")
                    .WithText("密码须为 8-32 位，且同时包含字母和数字"));
        }

        // 先查重给友好提示；并发下的唯一性仍由数据库唯一索引兜底
        var exists = await db.Users.AsNoTracking()
            .AnyAsync(u => u.UserName == userName, cancellationToken);
        if (exists)
        {
            return Result.Failure<RegisterResponse>(
                Error.Conflict("AUTH_CONFLICT_001", "auth.account_exists").WithText("账号已存在"));
        }

        var user = new User
        {
            UserName = userName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // 仓储写入在事务阶段的工作单元内执行，唯一索引冲突以失败结果返回
        var added = await users.AddAsync(user, cancellationToken);
        if (added.IsFailure)
        {
            return Result.Failure<RegisterResponse>(
                Error.Conflict("AUTH_CONFLICT_001", "auth.account_exists").WithText("账号已存在"));
        }

        return Result.Success(new RegisterResponse(user.Id));
    }
}

/// <summary>注册请求</summary>
/// <param name="UserName">账号：4-20 位字母、数字或下划线</param>
/// <param name="Password">密码：8-32 位，须同时包含字母和数字</param>
public sealed record RegisterRequest(string UserName, string Password);

/// <summary>注册响应</summary>
/// <param name="UserId">新用户的数据库 ID</param>
public sealed record RegisterResponse(long UserId);
#endregion

#region 图形验证码
/// <summary>
/// 图形验证码操作：登录前的第一道人机校验，SVG 以 data URI 返回（前端可直接 &lt;img src&gt;）。
/// 契约 ID auth.captcha → GET /auth:captcha（whoami 同款非标准动词，ReadOnly 标记只读可重复调用）
/// </summary>
[Operation(CaptchaOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    ReadOnly = true)]
public sealed class CaptchaOperation(CaptchaStore captchas) : IOperation<CaptchaRequest, CaptchaResponse>
{
    public const string Id = "auth.captcha";

    public async ValueTask<Result<CaptchaResponse>> HandleAsync(CaptchaRequest request, InvocationContext context)
    {
        var (id, image) = await captchas.CreateAsync(context.CancellationToken);
        return Result.Success(new CaptchaResponse(id, image, CaptchaStore.ExpiresIn));
    }
}

/// <summary>验证码请求（空载荷）</summary>
public sealed record CaptchaRequest;

/// <summary>验证码响应</summary>
/// <param name="CaptchaId">验证码 ID，登录时随 captchaCode 一起提交</param>
/// <param name="Image">SVG 图像的 data URI，前端可直接作为 &lt;img src&gt; 展示</param>
/// <param name="ExpiresIn">有效期秒数（2 分钟），过期或尝试一次后作废</param>
public sealed record CaptchaResponse(string CaptchaId, string Image, int ExpiresIn);
#endregion

#region 登录
/// <summary>
/// 登录操作：契约 ID auth.login → HTTP 适配器自动映射 POST /auth:login。
/// 双轨会话：签发 JWT（验签轨）的同时把 jti 会话写进 Redis（吊销轨）。
/// 登录须先验图形验证码（读后即删），通过后才查账号密码
/// </summary>
[Operation(LoginOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http)]
public sealed class LoginOperation(AuthDbContext db, JwtTokenIssuer tokens, SessionStore sessions, CaptchaStore captchas)
    : IOperation<LoginRequest, LoginResponse>
{
    public const string Id = "auth.login";

    // 防时序侧信道：账号不存在时短路跳过了 BCrypt.Verify（它故意很慢，几十毫秒级），
    // 响应时间会暴露「账号是否存在」，与同码防枚举相悖。
    // 预生成一个固定假哈希（与业务哈希同工作因子），账号不存在时也对提交的密码跑一次
    // Verify，让「账号不存在」与「密码错误」耗时一致，无法凭响应时间枚举账号
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("timing-equalizer:no-such-user");

    public async ValueTask<Result<LoginResponse>> HandleAsync(LoginRequest request, InvocationContext context)
    {
        // 第一道：图形验证码（读后即删，一次有效；不过不烧 BCrypt）。先删后比对，防并发重放
        if (!await captchas.VerifyAndConsumeAsync(request.CaptchaId, request.CaptchaCode, context.CancellationToken))
        {
            return Result.Failure<LoginResponse>(
                Error.Validation("AUTH_VALIDATION_003", "auth.captcha_invalid")
                    .WithText("验证码错误或已过期，请刷新后重试"));
        }

        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName == request.UserName.Trim(), context.CancellationToken);

        // 账号不存在与密码错误同码返回，防账号枚举；
        // 且无论账号是否存在都恰好执行一次 BCrypt.Verify（不存在时对假哈希），耗时一致，
        // 攻击者无法凭响应时间差枚举账号（BCrypt 故意很慢，几十毫秒级）
        var hash = user?.PasswordHash ?? DummyHash;
        var verified = BCrypt.Net.BCrypt.Verify(request.Password, hash);
        if (user is null || !verified)
        {
            return Result.Failure<LoginResponse>(
                Error.Denied("AUTH_DENIED_001", "auth.credentials_invalid").WithText("账号或密码错误"));
        }

        // 签发 HS256 JWT（sub=用户 ID，jti=会话 ID，有效期 7 天），并登记 Redis 会话：
        // 验签由内核 Authenticate 阶段做，会话有效性由 SessionGateHandler 查 Redis
        var (token, jti, expiresIn) = tokens.Issue(user.Id, user.UserName);
        await sessions.SaveAsync(jti, user.Id.ToString(), context.CancellationToken);
        return Result.Success(new LoginResponse(token, expiresIn));
    }
}

/// <summary>登录请求</summary>
/// <param name="CaptchaId">图形验证码 ID（GET /auth:captcha 返回）</param>
/// <param name="CaptchaCode">用户输入的验证码字符（忽略大小写），一次有效</param>
/// <param name="UserName">账号</param>
/// <param name="Password">密码（明文，HTTPS 传输）</param>
public sealed record LoginRequest(string CaptchaId, string CaptchaCode, string UserName, string Password);

/// <summary>登录响应</summary>
/// <param name="Token">会话令牌，有效期 7 天</param>
/// <param name="ExpiresIn">有效期秒数</param>
public sealed record LoginResponse(string Token, int ExpiresIn);
#endregion

#region 查询当前身份
/// <summary>
/// 查询当前身份 操作：受保护接口示例，Policy=authenticated 要求登录（JWT 校验生效才有主体）。
/// 契约 ID auth.whoami → GET /auth:whoami（whoami 非标准动词，ReadOnly 标记只读可重复调用）
/// </summary>
[Operation(WhoAmIOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    ReadOnly = true, Policy = "authenticated")]
public sealed class WhoAmIOperation : IOperation<WhoAmIRequest, WhoAmIResponse>
{
    public const string Id = "auth.whoami";

    public ValueTask<Result<WhoAmIResponse>> HandleAsync(WhoAmIRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;

        return ValueTask.FromResult(Result.Success(new WhoAmIResponse(
            principal.IsAuthenticated,
            principal.IsAuthenticated ? principal.Id : null,
            principal.AuthenticationScheme,
            principal.DisplayName)));
    }
}

/// <summary>查询当前身份请求（空载荷）</summary>
public sealed record WhoAmIRequest;

/// <summary>查询当前身份响应：当前调用方身份</summary>
/// <param name="IsAuthenticated">是否已认证</param>
/// <param name="Id">主体 ID（用户 ID），未认证为 null</param>
/// <param name="Scheme">认证方案</param>
/// <param name="DisplayName">显示名（账号）</param>
public sealed record WhoAmIResponse(bool IsAuthenticated, string? Id, string? Scheme, string? DisplayName);
#endregion

#region 登出
/// <summary>
/// 登出操作：吊销当前令牌的会话（双轨的吊销轨），令牌本身验签仍通过但门禁查不到会话即失效。
/// 契约 ID auth.logout → POST /auth:logout（请求体传 {}）
/// </summary>
[Operation(LogoutOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    Policy = "authenticated")]
public sealed class LogoutOperation(SessionStore sessions) : IOperation<LogoutRequest, LogoutResponse>
{
    public const string Id = "auth.logout";

    public async ValueTask<Result<LogoutResponse>> HandleAsync(LogoutRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;
        var jti = principal.ClaimValues("jti").FirstOrDefault();

        // 走到这里说明会话门禁已放行，jti 必然存在；防御式兜底
        if (jti is null || principal.Id is null)
        {
            return Result.Failure<LogoutResponse>(SessionGateHandler.SessionInvalid());
        }

        await sessions.RevokeAsync(jti, principal.Id, context.CancellationToken);
        return Result.Success(new LogoutResponse(true));
    }
}

/// <summary>登出请求（空载荷，令牌从 Authorization 头取）</summary>
public sealed record LogoutRequest;

/// <summary>登出响应</summary>
/// <param name="Revoked">会话是否已吊销（当前令牌此后立即失效）</param>
public sealed record LogoutResponse(bool Revoked);
#endregion

#region 强制下线
/// <summary>
/// 强制下线操作：吊销当前账号在所有设备的会话（改密码 / 疑似被盗时用）。
/// 契约 ID auth.logout-all → POST /auth:logout-all（请求体传 {}，含发起本次调用的设备）
/// </summary>
[Operation(LogoutAllOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http,
    Policy = "authenticated")]
public sealed class LogoutAllOperation(SessionStore sessions) : IOperation<LogoutAllRequest, LogoutAllResponse>
{
    public const string Id = "auth.logout-all";

    public async ValueTask<Result<LogoutAllResponse>> HandleAsync(LogoutAllRequest request, InvocationContext context)
    {
        var principal = context.GetSecurity().Principal;

        if (principal.Id is null)
        {
            return Result.Failure<LogoutAllResponse>(SessionGateHandler.SessionInvalid());
        }

        var revoked = await sessions.RevokeAllAsync(principal.Id, context.CancellationToken);
        return Result.Success(new LogoutAllResponse(revoked));
    }
}

/// <summary>强制下线请求（空载荷，吊销当前账号的全部会话）</summary>
public sealed record LogoutAllRequest;

/// <summary>强制下线响应</summary>
/// <param name="Revoked">吊销的会话数（含发起本次调用的设备）</param>
public sealed record LogoutAllResponse(int Revoked);
#endregion
