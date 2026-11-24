using AuthBaseXiHan.Sessions.Captcha;
using AuthBaseXiHan.Sessions.Jwt;
using XiHan.Framework.Caching.Abstractions.Caches;
using XiHan.Framework.Caching.Abstractions.Stores;

namespace AuthBaseXiHan.Sessions.Session;

/// <summary>
/// 通用凭据登录流：auth 与 admin 登录的共享实现（验证码 → 查账号 → 防时序比对 → 会话登记）。
/// </summary>
/// <remarks>
/// 两套登录的差异只有三处，全部由调用方注入 / 自行映射，流程本体只写一遍：
/// - 账号来源：lookup 委托查各自表（Users / AdminUsers），返回带密码哈希与角色的账号档案；
/// - 错误码：登录失败只返回 <see cref="CredentialLoginFailure"/> 种类，AUTH_* / ADMIN_* 由各自操作映射；
/// - 会话轨道：scope 决定 Redis 用户索引键命名空间与 token_kind claim。
/// 防账号枚举三件套在这里统一做：验证码读后即删、账号不存在与密码错误同码种类
/// （CredentialsInvalid）、无论账号是否存在都恰好执行一次 BCrypt.Verify（假哈希垫耗时）。
/// </remarks>
public sealed class CredentialAuthenticator(CaptchaStore captchas, JwtTokenIssuer tokens, SessionStore sessions)
{
    /// <summary>防时序侧信道假哈希：BCrypt 故意很慢（几十毫秒级），账号不存在时对它跑一次 Verify，
    /// 让「账号不存在」与「密码错误」耗时一致，无法凭响应时间差枚举账号</summary>
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("timing-equalizer:no-such-account");

    /// <summary>登录成功结果：会话令牌与账号档案</summary>
    /// <param name="Token">会话令牌（7 天）</param>
    /// <param name="ExpiresIn">有效期秒数</param>
    /// <param name="Account">账号档案（含 ID / 账号 / 角色，供操作组装自己的响应）</param>
    public sealed record LoginOutcome(string Token, int ExpiresIn, VerifiedAccount Account);

    /// <summary>登录核验过的账号档案</summary>
    /// <param name="Id">主体 ID（数据库主键）</param>
    /// <param name="UserName">账号</param>
    /// <param name="Roles">签发到令牌的角色（不含 Ai:AdminUsers 配置注入的 llm.admin）</param>
    public sealed record VerifiedAccount(long Id, string UserName, IReadOnlyList<string> Roles);

    /// <summary>登录失败种类：操作层按自己的错误码前缀映射（同码防枚举由调用方保持）</summary>
    public enum FailureKind
    {
        /// <summary>验证码错误或已过期（读后即删，一次有效）</summary>
        CaptchaInvalid,

        /// <summary>账号不存在或密码错误（两种情况同一种类，防枚举）</summary>
        CredentialsInvalid,

        /// <summary>账号已停用（仅 AdminUsers 有此状态；文案是否与密码错误同码由调用方决定）</summary>
        Disabled,
    }

    /// <summary>登录结果：成功带 <see cref="LoginOutcome"/>，失败带 <see cref="FailureKind"/></summary>
    public sealed record LoginResult(FailureKind? Failure, LoginOutcome? Outcome);

    /// <summary>账号查找委托：按账号名查各自的表，查无此人返回 null</summary>
    public delegate ValueTask<AccountRecord?> LookupAsync(string userName, CancellationToken cancellationToken);

    /// <summary>账号记录：lookup 从表实体投影而来，流程本体不依赖具体实体类型</summary>
    /// <param name="Id">主体 ID</param>
    /// <param name="UserName">账号</param>
    /// <param name="PasswordHash">BCrypt 哈希</param>
    /// <param name="Roles">签发到令牌的角色</param>
    /// <param name="IsActive">是否启用（Users 表恒 true；AdminUsers 可停用）</param>
    public sealed record AccountRecord(long Id, string UserName, string PasswordHash, IReadOnlyList<string> Roles, bool IsActive);

    /// <summary>
    /// 执行通用登录流：验证码 → 查账号 → 防时序比对 → 停用检查 → 签发令牌并登记会话。
    /// </summary>
    /// <param name="captchaId">验证码 ID</param>
    /// <param name="captchaCode">用户输入的验证码</param>
    /// <param name="userName">账号（调用方保证已去除首尾空白）</param>
    /// <param name="password">明文密码</param>
    /// <param name="scope">会话轨道（决定索引键命名空间与 token_kind claim）</param>
    /// <param name="lookup">账号查找委托</param>
    public async ValueTask<LoginResult> LoginAsync(
        string captchaId, string captchaCode, string userName, string password,
        SessionScope scope, LookupAsync lookup, CancellationToken cancellationToken)
    {
        // 第一道：图形验证码（读后即删，一次有效；不过不烧 BCrypt）。先删后比对，防并发重放
        if (!await captchas.VerifyAndConsumeAsync(captchaId, captchaCode, cancellationToken))
        {
            return new LoginResult(FailureKind.CaptchaInvalid, null);
        }

        var account = await lookup(userName, cancellationToken);

        // 账号不存在与密码错误同码种类，防账号枚举；耗时对齐见 DummyHash 注释
        var hash = account?.PasswordHash ?? DummyHash;
        var verified = BCrypt.Net.BCrypt.Verify(password, hash);
        if (account is null || !verified)
        {
            return new LoginResult(FailureKind.CredentialsInvalid, null);
        }

        if (!account.IsActive)
        {
            return new LoginResult(FailureKind.Disabled, null);
        }

        // 签发 HS256 JWT（带 token_kind 会话类型）并登记 Redis 会话（对应轨道索引）
        var kind = scope == SessionScope.Admin ? JwtTokenIssuer.AdminKind : JwtTokenIssuer.UserKind;
        var (token, jti, expiresIn) = tokens.Issue(account.Id, account.UserName, kind, account.Roles);
        await sessions.SaveAsync(scope, jti, account.Id.ToString(), cancellationToken);

        return new LoginResult(null, new LoginOutcome(token, expiresIn,
            new VerifiedAccount(account.Id, account.UserName, account.Roles)));
    }
}
