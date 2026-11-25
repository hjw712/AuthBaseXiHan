using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>限流判定结果</summary>
/// <param name="Allowed">是否放行</param>
/// <param name="RetryAfterSeconds">拒绝时建议的等待秒数</param>
/// <param name="Scope">拒绝命中的配额：minute / day</param>
public readonly record struct AiRateLimitDecision(bool Allowed, int RetryAfterSeconds, string? Scope);

/// <summary>
/// 每用户模型调用配额：分钟桶 + 自然日桶（UTC），进程内计数。
/// </summary>
/// <remarks>
/// 为什么不复用框架内置限流：内核 `ResilienceMiddleware` 按**操作 ID**限流，是全局计数，
/// 无法区分调用方；文档也明确「按调用方分桶不在内核范围」。按用户限流必须在能拿到主体的地方做，
/// 所以放在操作内部（Authenticate 之后）。
///
/// 为什么不用 Redis 做跨实例配额：框架的 ISharedCache 没有暴露原子递增（INCR/EXPIRE），
/// 用它拼「读-改-写」窗口在并发下会低估计数，反而不如老老实实按实例计数；
/// 多副本部署时实际上限 = 单实例上限 × 副本数，与框架内置限流的取舍一致。
///
/// 内存边界：按用户 ID 建桶，条目数随活跃用户增长。清理策略是每次判定顺手淘汰过期桶
/// （日桶不是今天、分钟桶早已过去的），并在条目数超过阈值时做一次全量扫除，
/// 因此字典大小上限与「最近一个窗口内的活跃用户数」同阶，不会被历史用户撑爆。
/// </remarks>
public sealed class AiUserRateLimiter(IOptions<AiChatOptions> options)
{
    /// <summary>条目数超过此值且距上次全量清理超过一分钟时做一次扫除</summary>
    private const int SweepThreshold = 10_000;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private long _lastSweepTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>一个用户的计数窗口（minute 桶按 Unix 分钟序号，day 桶按 UTC 日期）</summary>
    private sealed class Window
    {
        public long MinuteIndex;
        public int MinuteCount;
        public DateOnly Day;
        public int DayCount;
    }

    /// <summary>
    /// 判定并占用一次配额。放行时计数加一；拒绝时不动计数。
    /// </summary>
    public AiRateLimitDecision Check(string userId, DateTimeOffset now)
    {
        var opts = options.Value;

        // 两个配额都没配就等于关闭限流，直接放行，不建桶
        if (opts.UserMinuteLimit <= 0 && opts.UserDailyLimit <= 0)
        {
            return new AiRateLimitDecision(true, 0, null);
        }

        SweepIfNeeded(now);

        var window = _windows.GetOrAdd(userId, static _ => new Window());
        var minuteIndex = now.ToUnixTimeSeconds() / 60;
        var today = DateOnly.FromDateTime(now.UtcDateTime);

        // 锁粒度是单个用户：不同用户互不阻塞，同一用户的判定串行（避免并发下计数丢更新）
        lock (window)
        {
            if (window.MinuteIndex != minuteIndex)
            {
                window.MinuteIndex = minuteIndex;
                window.MinuteCount = 0;
            }

            if (window.Day != today)
            {
                window.Day = today;
                window.DayCount = 0;
            }

            // 分钟配额先判：它恢复得快，先告诉调用方等几十秒而不是等一天
            if (opts.UserMinuteLimit > 0 && window.MinuteCount >= opts.UserMinuteLimit)
            {
                var secondsIntoMinute = (int)(now.ToUnixTimeSeconds() % 60);
                return new AiRateLimitDecision(false, Math.Max(1, 60 - secondsIntoMinute), "minute");
            }

            if (opts.UserDailyLimit > 0 && window.DayCount >= opts.UserDailyLimit)
            {
                return new AiRateLimitDecision(false, SecondsUntilUtcMidnight(now), "day");
            }

            window.MinuteCount++;
            window.DayCount++;
            return new AiRateLimitDecision(true, 0, null);
        }
    }

    /// <summary>距 UTC 零点还有多少秒（自然日配额的重置时刻）</summary>
    private static int SecondsUntilUtcMidnight(DateTimeOffset now)
    {
        var remaining = now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime;
        return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
    }

    private void SweepIfNeeded(DateTimeOffset now)
    {
        var ticks = now.UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (_windows.Count <= SweepThreshold || ticks - last < SweepInterval.Ticks)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSweepTicks, ticks, last) != last)
        {
            return; // 别的线程已经在扫
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var staleMinute = now.ToUnixTimeSeconds() / 60 - 1;

        foreach (var pair in _windows)
        {
            lock (pair.Value)
            {
                // 两个桶都过期才算可淘汰：保留「今天已用满、当前分钟空闲」的桶，否则日配额会被重置
                if (pair.Value.Day != today && pair.Value.MinuteIndex < staleMinute)
                {
                    _windows.TryRemove(pair.Key, out _);
                }
            }
        }
    }
}