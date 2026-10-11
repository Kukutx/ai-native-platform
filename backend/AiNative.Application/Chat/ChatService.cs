using System.Text;
using System.Threading.Channels;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Agents.Runtime;
using AiNative.Application.Ai;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Chat;
using AiNative.Domain.Chat;
using AiNative.Domain.Governance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Chat;

/// <summary>
/// 工作台对话：流式输出文本，模型可以调用当前成员有权限的平台动作（权限与界面操作完全一致）。
/// </summary>
public interface IChatService
{
    IAsyncEnumerable<ChatStreamEvent> SendAsync(Guid conversationId, string text, CancellationToken cancellationToken);
}

internal sealed class ChatService(ITenantContext tenant, ITenantScopeFactory scopes, ILogger<ChatService> logger) : IChatService
{
    public async IAsyncEnumerable<ChatStreamEvent> SendAsync(
        Guid conversationId,
        string text,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var tenantId = tenant.TenantId;
        var actor = tenant.Actor;
        var features = tenant.Features.ToArray();

        // 生产者在独立作用域里跑完整轮对话，连接中断时由取消令牌结束；异常转换成 error 事件。
        var producer = Task.Run(async () =>
        {
            try
            {
                await scopes.RunAsync(tenantId, actor, features, async services =>
                {
                    await services.GetRequiredService<ChatTurnRunner>()
                        .RunAsync(conversationId, text, channel.Writer, cancellationToken).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (AppException ex)
            {
                channel.Writer.TryWrite(new ChatStreamEvent("error", Text: ex.Message));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat turn failed for conversation {ConversationId}", conversationId);
                channel.Writer.TryWrite(new ChatStreamEvent("error", Text: "对话出错了，请稍后重试。"));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }

        await producer.ConfigureAwait(false);
    }
}

internal sealed class ChatTurnRunner(
    IAppDbContext db,
    ITenantContext tenant,
    IModelGateway models,
    IActionInvoker actions,
    ITenantScopeFactory scopes,
    AgentPromptBuilder prompts,
    IChangeFeed changes,
    TimeProvider clock)
{
    private const int MaxSteps = 8;
    private const int MaxOutputTokens = 16_000;

    public async Task RunAsync(Guid conversationId, string text, ChannelWriter<ChatStreamEvent> output, CancellationToken cancellationToken)
    {
        var userId = tenant.Actor is { IsUser: true, Id: { } id } ? id : throw new ForbiddenException("只有成员可以使用对话。");
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, cancellationToken).ConfigureAwait(false)
                           ?? throw new NotFoundException("conversation", conversationId);
        var agent = conversation.AgentId is { } agentId
            ? await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false)
            : null;
        var history = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var seq = history.Count == 0 ? 0 : history[^1].Seq;

        var userName = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync(cancellationToken).ConfigureAwait(false);
        var system = agent is null
            ? GeneralAssistantPrompt(userName)
            : await prompts.BuildSystemPromptAsync(agent, cancellationToken).ConfigureAwait(false)
              + $"\n\n## 当前场景\n你正在工作台与成员 {userName} 直接对话。按对方的要求回答或动手；操作以对方的权限执行。";
        List<ChatMessage> messages = [new(ChatRole.System, system)];
        messages.AddRange(history.Select(ChatMapping.Deserialize).OfType<ChatMessage>());

        var userMessage = new ChatMessage(ChatRole.User, text.Trim());
        messages.Add(userMessage);
        await PersistAsync(conversation.Id, ++seq, userMessage, cancellationToken).ConfigureAwait(false);

        var provider = agent?.ModelProvider ?? models.Default.Provider;
        var model = agent?.Model ?? models.Default.Model;
        var client = models.GetClient(provider, model);
        var toolset = await ActionToolset.CreateAsync(actions, cancellationToken).ConfigureAwait(false);
        var options = new ChatOptions { ModelId = model, MaxOutputTokens = MaxOutputTokens, Tools = toolset.Tools, ToolMode = ChatToolMode.Auto };

        Guid? lastAssistantId = null;
        for (var step = 0; step < MaxSteps; step++)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
                if (!string.IsNullOrEmpty(update.Text))
                {
                    await output.WriteAsync(new ChatStreamEvent("text_delta", Text: update.Text), cancellationToken).ConfigureAwait(false);
                }
            }

            var response = updates.ToChatResponse();
            await RecordCostAsync(conversation.Id, provider, model, response.Usage, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                messages.Add(message);
                lastAssistantId = await PersistAsync(conversation.Id, ++seq, message, cancellationToken).ConfigureAwait(false);
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                break;
            }

            var results = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                var action = toolset.Resolve(call.Name);
                var actionKey = action?.Key ?? call.Name;
                await output.WriteAsync(
                    new ChatStreamEvent("tool_call", CallId: call.CallId, ActionKey: actionKey, Data: PlatformJson.ToElement(call.Arguments)),
                    cancellationToken).ConfigureAwait(false);
                var result = action is null
                    ? new ActionInvocationResult(ActionInvocationStatus.NotFound, Error: $"没有名为 {call.Name} 的工具。")
                    : await ActionToolset.InvokeAsync(scopes, tenant.TenantId, tenant.Actor, tenant.Features, action.Key, call.Arguments, new ActionInvocationOptions(), cancellationToken)
                        .ConfigureAwait(false);
                var formatted = ActionToolset.FormatForModel(result);
                await output.WriteAsync(
                    new ChatStreamEvent("tool_result", CallId: call.CallId, ActionKey: actionKey, Data: PlatformJson.Parse(formatted)),
                    cancellationToken).ConfigureAwait(false);
                results.Add(new FunctionResultContent(call.CallId, formatted));
            }

            var toolMessage = new ChatMessage(ChatRole.Tool, results);
            messages.Add(toolMessage);
            await PersistAsync(conversation.Id, ++seq, toolMessage, cancellationToken).ConfigureAwait(false);
        }

        conversation.LastMessageAt = clock.GetUtcNow();
        if (conversation.Title == "新对话")
        {
            var title = text.Trim().ReplaceLineEndings(" ");
            conversation.Title = title.Length > 30 ? title[..30] + "…" : title;
        }

        changes.Publish(RealtimeEntities.Conversation, conversation.Id, RealtimeChangeKind.Updated);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(new ChatStreamEvent("done", MessageId: lastAssistantId), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> PersistAsync(Guid conversationId, int seq, ChatMessage message, CancellationToken cancellationToken)
    {
        var row = new ConversationMessage
        {
            ConversationId = conversationId,
            Seq = seq,
            Role = message.Role.Value,
            ContentJson = ChatMapping.Serialize(message),
            Text = message.Text ?? string.Empty,
        };
        db.ConversationMessages.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return row.Id;
    }

    private async Task RecordCostAsync(Guid conversationId, string provider, string model, UsageDetails? usage, CancellationToken cancellationToken)
    {
        var input = usage?.InputTokenCount ?? 0;
        var outputTokens = usage?.OutputTokenCount ?? 0;
        db.CostEvents.Add(new CostEvent
        {
            Actor = tenant.Actor,
            ConversationId = conversationId,
            Provider = provider,
            Model = model,
            InputTokens = input,
            OutputTokens = outputTokens,
            CostUsd = models.EstimateCostUsd(provider, model, input, outputTokens),
            OccurredAt = clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string GeneralAssistantPrompt(string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是 AI Native 平台的工作台助手，正在与成员 {userName} 对话。");
        sb.AppendLine("""
            - 你可以通过工具查询和操作平台数据（任务、智能体团队等），权限与该成员在界面上的权限完全一致。
            - 需要动手时直接调用工具，完成后用简短的话说明结果；涉及多步操作先说明计划。
            - 想让智能体员工去做的事，创建任务并指派给合适的智能体，它会被自动唤醒。
            - 不确定时先查询再回答，不要编造数据。
            - 用对方使用的语言回复。
            """);
        return sb.ToString();
    }
}
