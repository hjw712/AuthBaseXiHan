using AuthBaseXiHan.Sessions.Jwt;
using XiHan.Framework.Caching.Abstractions.Caches;
using XiHan.Framework.Caching.Abstractions.Stores;

namespace AuthBaseXiHan.Sessions.Session;

/// <summary>
/// 会话存储：JWT + Redis 双轨的 Redis 轨，同一套流程服务两类主体（用户 / 管理员）
/// </summary>
/// <remarks>
/// JWT 验签是无状态的，无法主动吊销；本存储以 jti（令牌 ID）为键在 Redis 记录有效会话，
/// 形成「白名单」：<see cref="SessionGateHandler"/> 验签通过后再查会话存在才放行，
/// 登出/强制下线删掉 Redis 键即立即失效，令牌本身不用改。
///
/// 键设计（应用级前缀 authbase: 由框架统一追加）：
/// - <c>auth:sessions:{jti}</c> → 主体 ID（string），两类主体共用（键是随机 GUID 不可能冲突；
///   门禁只查会话键存在性，与轨道无关），TTL 与令牌一致（7 天）
/// - <c>auth:sessions:user:{id}</c> / <c>auth:sessions:admin:{id}</c> → 该主体的 jti 列表（string[]），
///   强制下线用。两套账号是两张独立表、long 主键各自自增会撞号，索引必须分开命名空间，
///   否则强制下线会把「用户 1 号」和「管理员 1 号」的会话混在一起误吊销。
///   列表里的过期 jti 平时不清理（会话键到期自然消失，多留一条无害），
///   强制下线时按存在性过滤顺带清除
///
/// 代价：每个受保护请求多一次 Redis 读（约亚毫秒级），换来立即吊销与多端管理能力。
/// </remarks>
public sealed class SessionStore(ISharedCache cache)
{
    /// <summary>会话 TTL 与令牌有效期一致：令牌过期会话自动消失，不需要额外清理</summary>
    private static TimeSpan TimeToLive => TimeSpan.FromSeconds(JwtTokenIssuer.ExpiresInSeconds);

    private static CacheKey SessionKey(string jti) => new($"auth:sessions:{jti}");

    private static CacheKey IndexKey(SessionScope scope, string userId) => scope switch
    {
        SessionScope.Admin => new($"auth:sessions:admin:{userId}"),
        _ => new($"auth:sessions:user:{userId}"),
    };

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
    public async ValueTask SaveAsync(SessionScope scope, string jti, string userId, CancellationToken cancellationToken)
    {
        var indexKey = IndexKey(scope, userId);
        var index = await cache.GetAsync<string[]>(indexKey, cancellationToken);
        var jtis = index.TryGetValue(out var list) && list is not null ? [.. list, jti] : new[] { jti };
        await cache.SetAsync(indexKey, jtis, CacheEntryOptions.Expires(TimeToLive), cancellationToken);

        await cache.SetAsync(SessionKey(jti), userId, CacheEntryOptions.Expires(TimeToLive), cancellationToken);
    }

    /// <summary>会话是否有效：会话键存在且归属者与主体一致（门禁处理器用，与轨道无关）</summary>
    public async ValueTask<bool> ExistsAsync(string jti, string userId, CancellationToken cancellationToken)
    {
        var session = await cache.GetAsync<string>(SessionKey(jti), cancellationToken);
        return session.TryGetValue(out var owner) && owner == userId;
    }

    /// <summary>吊销单个会话（登出当前设备）：删会话键并尽力从索引摘除</summary>
    public async ValueTask RevokeAsync(SessionScope scope, string jti, string userId, CancellationToken cancellationToken)
    {
        await cache.RemoveAsync(SessionKey(jti), cancellationToken);

        var indexKey = IndexKey(scope, userId);
        var index = await cache.GetAsync<string[]>(indexKey, cancellationToken);
        if (index.TryGetValue(out var list) && list is not null)
        {
            var remaining = list.Where(existing => existing != jti).ToArray();
            await cache.SetAsync(indexKey, remaining, CacheEntryOptions.Expires(TimeToLive), cancellationToken);
        }
    }

    /// <summary>吊销该主体全部会话（强制下线 / 管理侧停用删除时清场），返回真实吊销的会话数</summary>
    /// <remarks>
    /// 索引可能残留已过期 / 已登出 / 崩溃遗留的孤儿 jti（登录先写索引、后写会话键），
    /// 逐个确认会话键仍存在才计入吊销数并删除——顺带把这些残留清出了索引。
    /// </remarks>
    public async ValueTask<int> RevokeAllAsync(SessionScope scope, string userId, CancellationToken cancellationToken)
    {
        var indexKey = IndexKey(scope, userId);
        var index = await cache.GetAsync<string[]>(indexKey, cancellationToken);
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

        await cache.RemoveAsync(indexKey, cancellationToken);
        return live.Count;
    }
}

/// <summary>会话轨道：用户（Users 表）与管理员（AdminUsers 表）各一条，只差用户索引键命名空间</summary>
public enum SessionScope
{
    /// <summary>普通用户会话（auth 模块，索引键 auth:sessions:user:{id}）</summary>
    User,

    /// <summary>管理员会话（admin 模块，索引键 auth:sessions:admin:{id}）</summary>
    Admin,
}
