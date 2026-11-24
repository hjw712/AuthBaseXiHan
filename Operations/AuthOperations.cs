using System.Text.RegularExpressions;
using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions.Session;
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

#region 登录
/// <summary>用户登录请求（与 admin.login 的契约各自独立，互不引用）</summary>
/// <param name="CaptchaId">图形验证码 ID（GET /common:captcha 返回，两轨共用同一验证码服务）</param>
/// <param name="CaptchaCode">用户输入的验证码字符（忽略大小写），一次有效</param>
/// <param name="UserName">账号</param>
/// <param name="Password">密码（明文，HTTPS 传输）</param>
public sealed record LoginRequest(string CaptchaId, string CaptchaCode, string UserName, string Password);

/// <summary>用户登录响应</summary>
/// <param name="Token">会话令牌，有效期 7 天（token_kind=user）</param>
/// <param name="ExpiresIn">有效期秒数</param>
/// <param name="Roles">签发到令牌的角色（普通用户为空；llm.admin 由配置名单注入）</param>
public sealed record LoginResponse(string Token, int ExpiresIn, IReadOnlyList<string> Roles);

/// <summary>
/// 用户登录操作：契约 ID auth.login → HTTP 适配器自动映射 POST /auth:login。
/// 独立完整实现（与 admin.login 分开写，各自持有契约与实现，不共用基类）：
/// 流程调用 <see cref="CredentialAuthenticator"/>（验证码 + 防时序比对 + BCrypt + 会话登记），
/// 本轨差异：SessionScope.User 轨道、查 Users 表投影账号档案、AUTH_* 错误码映射。
/// 签发的令牌带 token_kind=user；whoami / logout / logout-all 单接口双轨通用，轨道从 claim 推导
/// </summary>
[Operation(LoginOperation.Id, Stability = Stability.Experimental, Expose = Expose.Http)]
public sealed class LoginOperation(AuthDbContext db, CredentialAuthenticator authenticator)
    : IOperation<LoginRequest, LoginResponse>
{
    public const string Id = "auth.login";

    public async ValueTask<Result<LoginResponse>> HandleAsync(LoginRequest request, InvocationContext context)
    {
        var result = await authenticator.LoginAsync(
            request.CaptchaId, request.CaptchaCode, request.UserName.Trim(), request.Password,
            SessionScope.User, LookupAsync, context.CancellationToken);

        if (result.Outcome is { } outcome)
        {
            return Result.Success(new LoginResponse(outcome.Token, outcome.ExpiresIn, outcome.Account.Roles));
        }

        // Outcome 为空时 Failure 必有值（LoginResult 的不变式）
        return Result.Failure<LoginResponse>(MapFailure(result.Failure!.Value));
    }

    // 账号来源：Users 表（无停用状态，IsActive 恒 true；无预置角色，llm.admin 由配置名单注入）
    private async ValueTask<CredentialAuthenticator.AccountRecord?> LookupAsync(
        string userName, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.UserName == userName, cancellationToken);
        return user is null
            ? null
            : new CredentialAuthenticator.AccountRecord(user.Id, user.UserName, user.PasswordHash, [], true);
    }

    // 失败种类 → AUTH_* 错误码；账号不存在 / 密码错误保持同码，防账号枚举
    private static Error MapFailure(CredentialAuthenticator.FailureKind failure) => failure switch
    {
        CredentialAuthenticator.FailureKind.CaptchaInvalid =>
            Error.Validation("AUTH_VALIDATION_003", "auth.captcha_invalid")
                .WithText("验证码错误或已过期，请刷新后重试"),
        _ => Error.Denied("AUTH_DENIED_001", "auth.credentials_invalid").WithText("账号或密码错误"),
    };
}
#endregion
