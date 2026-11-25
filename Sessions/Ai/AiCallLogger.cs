using AuthBaseXiHan.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthBaseXiHan.Sessions.Ai;

/// <summary>一次模型调用的计量记录（与实体解耦，便于调用方填一半字段）</summary>
/// <param name="TenantId">租户</param>
/// <param name="UserId">主体 ID</param>
/// <param name="Alias">模型别名</param>
/// <param name="PromptAlias">提示词别名</param>
/// <param name="Success">是否成功</param>
/// <param name="ErrorCode">失败错误码</param>
/// <param name="InputTokens">输入 token</param>
/// <param name="OutputTokens">输出 token</param>
/// <param name="ElapsedMs">耗时毫秒</param>
/// <param name="Question">问题（是否入库由 Ai:LogContent 决定）</param>
/// <param name="Answer">回答（同上）</param>
public sealed record AiCallRecord(
    string TenantId,
    string UserId,
    string Alias,
    string? PromptAlias,
    bool Success,
    string? ErrorCode,
    int InputTokens,
    int OutputTokens,
    long ElapsedMs,
    string? Question = null,
    string? Answer = null);

/// <summary>
/// 模型调用日志写入器：每次调用落一行 AiCallLog，用于 token 计费与用量统计。
/// </summary>
/// <remarks>
/// 为什么用独立的 DI 作用域而不是操作里那个 <see cref="AuthDbContext"/>：
/// 操作的写库由管道 Transaction 阶段的工作单元统一提交，**失败结果可能不回滚也可能回滚**，
/// 取决于实现细节；而最需要留下的恰恰是失败记录（超时、上游故障）。
/// 自己开作用域 + 立即 SaveChanges，提交时机由本类掌控，与操作的事务成败无关。
///
/// 为什么写日志失败只记 Warning 不外抛：计量是旁路能力，不能因为它挂掉就把一次成功的对话
/// 变成 500。日志里保留异常与关键字段，够定位。
///
/// 为什么用 <see cref="CancellationToken.None"/> 加独立 3 秒上限：正常路径传进来的请求令牌
/// 在超时/断开时已被取消，直接拿它写库会立刻抛取消异常——正好丢掉超时这条最该留的记录。
/// 独立短上限既保证写得完，又避免数据库缓慢时把响应拖住。
/// </remarks>
public sealed class AiCallLogger(
    IServiceScopeFactory scopes,
    IOptions<AiChatOptions> options,
    ILogger<AiCallLogger> logger)
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    /// <summary>落一条调用日志；本方法不抛异常</summary>
    public async ValueTask LogAsync(AiCallRecord record)
    {
        var opts = options.Value;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

            db.AiCallLogs.Add(new AiCallLog
            {
                TenantId = record.TenantId,
                UserId = record.UserId,
                Alias = Truncate(record.Alias, 50) ?? string.Empty,
                PromptAlias = Truncate(record.PromptAlias, 50),
                Success = record.Success,
                ErrorCode = Truncate(record.ErrorCode, 50),
                InputTokens = record.InputTokens,
                OutputTokens = record.OutputTokens,
                ElapsedMs = record.ElapsedMs,
                // 正文默认不落库：成本核算只需要 token 与次数，正文会扩大泄露面
                Question = opts.LogContent ? Truncate(record.Question, 500) : null,
                Answer = opts.LogContent ? Truncate(record.Answer, 2000) : null,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            using var cts = new CancellationTokenSource(WriteTimeout);
            await db.SaveChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            // 计量是旁路：失败只记日志，不影响调用方拿到的结果
            logger.LogWarning(ex,
                "写入模型调用日志失败：alias={Alias} user={UserId} success={Success}",
                record.Alias, record.UserId, record.Success);
        }
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];
}