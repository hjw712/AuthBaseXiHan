using System.Text;
using AuthBaseXiHan.Sessions.Session;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthBaseXiHan.Sessions.Jwt;

/// <summary>
/// JWT 签发服务（HS256 对称密钥）。
/// 框架的 Security.Jwt 只校验不签发：本服务用与 XiHan:Security:Jwt:Secret 相同的密钥签发，
/// 写入 sub（主体 ID）/ name（显示名）/ jti（令牌 ID，会话吊销的依据）/ exp（过期），
/// iss / aud 与配置一致——这样签出的令牌能直接通过内核管道 Authenticate 阶段的校验。
/// 双轨会话：JWT 轨负责验签（无状态），Redis 轨负责吊销（见 <see cref="SessionStore"/>），
/// SessionGateHandler 验签通过后查 Redis 会话存在才放行。
/// </summary>
public sealed class JwtTokenIssuer(IConfiguration configuration)
{
    /// <summary>令牌有效期（秒）：7 天</summary>
    public const int ExpiresInSeconds = 7 * 24 * 3600;

    /// <summary>会话类型 claim 名：user（用户会话）/ admin（管理员会话），跨轨道策略据此判定</summary>
    public const string TokenKindClaim = "token_kind";

    /// <summary>会话类型取值：用户会话</summary>
    public const string UserKind = "user";

    /// <summary>会话类型取值：管理员会话</summary>
    public const string AdminKind = "admin";

    private readonly JsonWebTokenHandler _handler = new();

    private SymmetricSecurityKey SigningKey => new(
        Encoding.UTF8.GetBytes(configuration["XiHan:Security:Jwt:Secret"]
            ?? throw new InvalidOperationException("缺少 JWT 密钥：请配置 XiHan:Security:Jwt:Secret")));

    private string Issuer => configuration["XiHan:Security:Jwt:Issuer"] ?? "authbase";

    private string Audience => configuration["XiHan:Security:Jwt:Audience"] ?? "authbase-api";

    /// <summary>
    /// 为指定主体签发 JWT。subject 取主体 ID（JwtAuthenticationHandler 按 NameClaim=sub 取主体 ID），
    /// jti 取随机 GUID（SessionStore 以它为键记录会话，登出/强制下线按它吊销）。
    /// </summary>
    /// <remarks>
    /// 会话类型：token_kind claim 标记会话轨道（user / admin，与 <see cref="Sessions.Session.SessionScope"/>
    /// 一一对应），门禁策略（AdminPolicy / AdminSuperPolicy）据此拒绝跨轨道调用；
    /// whoami / logout / logout-all 单接口双轨通用，轨道由 SessionScopes.FromTokenKind 从该 claim 推导。
    /// 管理员角色：配置 Ai:AdminUsers（逗号分隔账号）的用户会带上 role=llm.admin claim，
    /// 供 LlmAdminPolicy 判定模型配置管理权限。未配置时无人可管理凭据（安全默认）。
    /// </remarks>
    /// <param name="userId">主体 ID（写入 sub）</param>
    /// <param name="userName">账号（写入 name）</param>
    /// <param name="kind">会话类型：user（用户登录）/ admin（管理员登录）</param>
    /// <param name="extraRoles">额外角色声明（逐个写入 role claim），管理员登录时传 admin / admin.super / llm.admin</param>
    public (string Token, string Jti, int ExpiresIn) Issue(long userId, string userName,
        string kind = "user", IReadOnlyCollection<string>? extraRoles = null)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = userId.ToString(),
            [JwtRegisteredClaimNames.Name] = userName,
            [JwtRegisteredClaimNames.Jti] = jti,
            [TokenKindClaim] = kind,
        };

        // 角色收集进列表再整体写入：多值 claim（JSON 数组）在验签侧由 ClaimValues("role")
        // 逐条读出；若拼成单个字符串（"a,b"）会被当成一个角色值，策略 Contains 判定必失败
        var roles = new List<string>();

        // 管理员名单来自配置：逗号分隔账号，精确匹配（OrdinalIgnoreCase）
        var admins = configuration["Ai:AdminUsers"]?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     ?? [];
        if (admins.Contains(userName, StringComparer.OrdinalIgnoreCase))
        {
            roles.Add("llm.admin");
        }

        // 管理员等调用方自带的角色（配置名单之外的第二来源，如 admin 登录签发的 admin/admin.super）
        roles.AddRange(extraRoles ?? []);

        if (roles.Count > 0)
        {
            claims["role"] = roles;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = now.AddSeconds(ExpiresInSeconds).UtcDateTime,
            IssuedAt = now.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = claims,
        };

        return (_handler.CreateToken(descriptor), jti, ExpiresInSeconds);
    }
}
