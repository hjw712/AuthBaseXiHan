using AuthBaseXiHan.Persistence;
using AuthBaseXiHan.Sessions.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using XiHan.Framework.Kernel.Operations;
using XiHan.Framework.Kernel.Security;
using XiHan.Framework.Primitives;

namespace AuthBaseXiHan.Operations;

/// <summary>
/// 模型对话：契约 ID ai.chat → POST /ai:chat
/// 登录后可用；模型配置从数据库按租户+Alias 动态加载，不在 appsettings 写 Key
/// </summary>
/// <remarks>
/// 提示词与模型解耦：请求传 promptAlias 时按租户从 Prompts 表实时取该提示词作 system 消息
/// （改完立即生效，无缓存）；不传时退回模型配置的 SystemPrompt（该模型的默认提示词）。
/// 多轮历史本期先不接（阶段一的下一步）：当前是单轮问答。
/// token 计量记录在 ChatMessage 表（阶段三会话落库时一并实现）。
/// </remarks>
[Operation(AiChatOperation.Id, Expose = Expose.Http,
    Stability = Stability.Experimental, Policy = "authenticated")]
public sealed class AiChatOperation(AuthDbContext db, IChatClientFactory clients)
    : IOperation<AiChatRequest, AiChatResponse>
{
    public const string Id = "ai.chat";

    public async ValueTask<Result<AiChatResponse>> HandleAsync(AiChatRequest request, InvocationContext context)
    {
        // 问题格式校验
        var question = request.Question?.Trim() ?? string.Empty;
        if (question.Length is < 1 or > 2000)
        {
            return Result.Failure<AiChatResponse>(Error.Validation("AI_VALIDATION_001", "ai.question_empty")
                .WithText("问题不能为空或超过 2000 字符"));
        }

        // Alias 未指定时用 default；业务以后可以传 kb-chat 等别名切换模型
        var alias = string.IsNullOrWhiteSpace(request.Alias) ? "default" : request.Alias;
        var tenantId = context.CurrentTenantId();

        // 提示词解析：显式 promptAlias 优先（实时查库，改完立即生效）；不传用模型配置的默认提示词
        string? systemPrompt;
        if (!string.IsNullOrWhiteSpace(request.PromptAlias))
        {
            var promptAlias = request.PromptAlias.Trim();
            var prompt = await db.Prompts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Alias == promptAlias && p.IsActive,
                    context.CancellationToken);
            if (prompt is null)
            {
                return Result.Failure<AiChatResponse>(Error.Validation("AI_VALIDATION_002", "ai.prompt_not_found")
                    .WithText($"提示词不存在或未启用：{promptAlias}"));
            }

            systemPrompt = prompt.Content;
        }
        else
        {
            var (_, defaultPrompt) = await clients.CreateWithPromptAsync(alias, tenantId, context.CancellationToken);
            systemPrompt = defaultPrompt;
        }

        try
        {
            var client = await clients.CreateAsync(alias, tenantId, context.CancellationToken);

            // 有提示词时前置 system 消息（人设/边界/输出格式），再接用户问题
            var reply = string.IsNullOrEmpty(systemPrompt)
                ? await client.GetResponseAsync(question, cancellationToken: context.CancellationToken)
                : await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, question),
                ], cancellationToken: context.CancellationToken);

            var inputTokens = (int)(reply.Usage?.InputTokenCount ?? 0);
            var outputTokens = (int)(reply.Usage?.OutputTokenCount ?? 0);

            return Result.Success(new AiChatResponse(reply.Text ?? string.Empty, alias, inputTokens, outputTokens));
        }
        catch (Exception)
        {
            // 配置缺失/模型上游故障：上游错误可能回显 Key 片段，回显固定文案
            // 详细信息留给服务端日志（ObserveMiddleware 已记录完整异常与 traceId）
            return Result.Failure<AiChatResponse>(Error.Unexpected("AI_UNEXPECTED_001", "ai.model_error")
                .WithText("模型调用失败，请稍后重试或用 llm:testconfig 诊断"));
        }
    }
}

/// <summary>模型对话请求</summary>
/// <param name="Question">问题内容，1-2000 字符</param>
/// <param name="Alias">模型别名，不传用 default</param>
/// <param name="PromptAlias">提示词别名（Prompts 表，租户内）。传了用它作 system 消息；不传用模型配置的默认提示词</param>
public sealed record AiChatRequest(string Question, string? Alias = null, string? PromptAlias = null);

/// <summary>模型对话响应</summary>
/// <param name="Answer">模型回答</param>
/// <param name="Alias">实际使用的模型别名</param>
/// <param name="InputTokens">输入 token 数</param>
/// <param name="OutputTokens">输出 token 数</param>
public sealed record AiChatResponse(string Answer, string Alias, int InputTokens, int OutputTokens);
