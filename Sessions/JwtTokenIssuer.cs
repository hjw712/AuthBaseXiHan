using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthBaseXiHan.Sessions;

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

    private readonly JsonWebTokenHandler _handler = new();

    private SymmetricSecurityKey SigningKey => new(
        Encoding.UTF8.GetBytes(configuration["XiHan:Security:Jwt:Secret"]
            ?? throw new InvalidOperationException("缺少 JWT 密钥：请配置 XiHan:Security:Jwt:Secret")));

    private string Issuer => configuration["XiHan:Security:Jwt:Issuer"] ?? "authbase";

    private string Audience => configuration["XiHan:Security:Jwt:Audience"] ?? "authbase-api";

    /// <summary>
    /// 为指定用户签发 JWT。subject 取用户 ID（JwtAuthenticationHandler 按 NameClaim=sub 取主体 ID），
    /// jti 取随机 GUID（SessionStore 以它为键记录会话，登出/强制下线按它吊销）。
    /// </summary>
    public (string Token, string Jti, int ExpiresIn) Issue(long userId, string userName)
    {
        var now = DateTimeOffset.UtcNow;
        var jti = Guid.NewGuid().ToString("N");

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = now.AddSeconds(ExpiresInSeconds).UtcDateTime,
            IssuedAt = now.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [JwtRegisteredClaimNames.Name] = userName,
                [JwtRegisteredClaimNames.Jti] = jti,
            },
        };

        return (_handler.CreateToken(descriptor), jti, ExpiresInSeconds);
    }
}
