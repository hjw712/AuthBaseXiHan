using XiHan.Framework.Caching.Abstractions.Caches;
using XiHan.Framework.Caching.Abstractions.Stores;

namespace AuthBaseXiHan.Sessions.Captcha;

/// <summary>
/// 图形验证码存储：Redis 轨（登录双因素之一）
/// </summary>
/// <remarks>
/// 验证码 <c>auth:captchas:{id}</c> → 4 位字符（应用级前缀 authbase: 由框架统一追加），
/// TTL 2 分钟。<b>读后即删</b>：无论对错都作废，一次有效——防同一验证码重试爆破。
/// </remarks>
public sealed class CaptchaStore(ISharedCache cache)
{
    /// <summary>验证码有效期（秒）</summary>
    public const int ExpiresIn = 120;

    private static TimeSpan TimeToLive => TimeSpan.FromSeconds(ExpiresIn);

    private static CacheKey Key(string id) => new($"auth:captchas:{id}");

    /// <summary>生成新验证码：写进 Redis，返回 id 与 data URI 图像（code 不出存储，仅供校验）</summary>
    public async ValueTask<(string Id, string Image)> CreateAsync(CancellationToken cancellationToken)
    {
        var (id, code, svg) = CaptchaGenerator.New();
        await cache.SetAsync(Key(id), code, CacheEntryOptions.Expires(TimeToLive), cancellationToken);

        var image = "data:image/svg+xml;base64," + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg));
        return (id, image);
    }

    /// <summary>校验并消费：读后即删（一次有效），对错都作废；比对忽略大小写</summary>
    public async ValueTask<bool> VerifyAndConsumeAsync(string id, string code, CancellationToken cancellationToken)
    {
        var stored = await cache.GetAsync<string>(Key(id), cancellationToken);
        await cache.RemoveAsync(Key(id), cancellationToken);

        return stored.TryGetValue(out var expected)
            && string.Equals(expected, code, StringComparison.OrdinalIgnoreCase);
    }
}
