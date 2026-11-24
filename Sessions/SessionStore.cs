using XiHan.Framework.Caching.Abstractions.Caches;
using XiHan.Framework.Caching.Abstractions.Stores;

namespace AuthBaseXiHan.Sessions;

/// <summary>
/// 会话存储：JWT + Redis 双轨的 Redis 轨
/// </summary>
/// <remarks>
/// JWT 验签是无状态的，无法主动吊销；本存储以 jti（令牌 ID）为键在 Redis 记录有效会话，
/// 形成「白名单」：<see cref="SessionGateHandler"/> 验签通过后再查会话存在才放行，
/// 登出/强制下线删掉 Redis 键即立即失效，令牌本身不用改。
///
/// 键设计（应用级前缀 authbase: 由框架统一追加）：
/// - <c>auth:sessions:{jti}</c> → 用户 ID（string），TTL 与令牌一致（7 天）
/// - <c>auth:sessions:user:{userId}</c> → 该用户的 jti 列表（string[]），强制下线用，
///   每次登录重置 TTL；列表里的过期 jti 平时不清理（会话键到期自然消失，多留一条无害），
///   强制下线时按存在性过滤顺带清除
///
/// 代价：每个受保护请求多一次 Redis 读（约亚毫秒级），换来立即吊销与多端管理能力。
/// </remarks>
public sealed class SessionStore(ISharedCache cache)
{
    /// <summary>会话 TTL 与令牌有效期一致：令牌过期会话自动消失，不需要额外清理</summary>
    private static TimeSpan TimeToLive => TimeSpan.FromSeconds(JwtTokenIssuer.ExpiresInSeconds);

    private static CacheKey SessionKey(string jti) => new($"auth:sessions:{jti}");

    private static CacheKey UserIndexKey(string userId) => new($"auth:sessions:user:{userId}");

    /// <summary>登录成功后登记会话：先写索引、后写会话键</summary>
    /// <remarks>
    /// 顺序有讲究：进程在两次写之间崩溃时，只会留下「索引里有、会话键没有」的孤儿条目
    /// （登出无害，强制下线时会被存在性检查过滤，TTL 到期自然消失）；
    /// 反过来写会留下「会话键在、索引里没有」的活会话，强制下线永远漏掉它。
    ///
    /// 索引追加是「读-改-写」，并发登录下 last-write-wins，最坏情况丢一条索引
    /// （强制下线漏掉该端），会话键仍受 TTL 保护，模板场景可接受——ISharedCache
    /// 未暴露原子集合操作，无法在此消除竞态。
    /// </remarks>
    public async ValueTask SaveAsync(string jti, string userId, CancellationToken cancellationToken)
    {
        var index = await cache.GetAsync<string[]>(UserIndexKey(userId), cancellationToken);
        var jtis = index.TryGetValue(out var list) && list is not null ? [.. list, jti] : new[] { jti };
        await cache.SetAsync(UserIndexKey(userId), jtis, CacheEntryOptions.Expires(TimeToLive), cancellationToken);

        await cache.SetAsync(SessionKey(jti), userId, CacheEntryOptions.Expires(TimeToLive), cancellationToken);
    }

    /// <summary>会话是否有效：会话键存在且归属者与主体一致</summary>
    public async ValueTask<bool> ExistsAsync(string jti, string userId, CancellationToken cancellationToken)
    {
        var session = await cache.GetAsync<string>(SessionKey(jti), cancellationToken);
        return session.TryGetValue(out var owner) && owner == userId;
    }

    /// <summary>吊销单个会话（登出当前设备）：删会话键并尽力从索引摘除</summary>
    public async ValueTask RevokeAsync(string jti, string userId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(SessionKey(jti), cancellationToken);

        var index = await cache.GetAsync<string[]>(UserIndexKey(userId), cancellationToken);
        if (index.TryGetValue(out var list) && list is not null)
        {
            var remaining = list.Where(existing => existing != jti).ToArray();
            await cache.SetAsync(UserIndexKey(userId), remaining, CacheEntryOptions.Expires(TimeToLive), cancellationToken);
        }
    }

    /// <summary>吊销该用户全部会话（强制下线 / 退出所有设备），返回真实吊销的会话数</summary>
    /// <remarks>
    /// 索引可能残留已过期 / 已登出 / 崩溃遗留的孤儿 jti（登录先写索引、后写会话键），
    /// 逐个确认会话键仍存在才计入吊销数并删除——顺带把这些残留清出了索引。
    /// </remarks>
    public async ValueTask<int> RevokeAllAsync(string userId, CancellationToken cancellationToken)
    {
        var index = await cache.GetAsync<string[]>(UserIndexKey(userId), cancellationToken);
        var jtis = index.TryGetValue(out var list) && list is not null ? list : [];

        var live = new List<string>(jtis.Length);
        foreach (var jti in jtis)
        {
            if (await ExistsAsync(jti, userId, cancellationToken))
            {
                live.Add(jti);
            }
        }

        if (live.Count > 0)
        {
            await cache.RemoveManyAsync(
                live.Select(SessionKey).ToArray(),
                cancellationToken);
        }

        await cache.RemoveAsync(UserIndexKey(userId), cancellationToken);
        return live.Count;
    }
}
