using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Actions;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Chat;
using AiNative.Domain.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace AiNative.Application.Chat;

public sealed record ListConversationsQuery : IQuery<IReadOnlyList<ConversationDto>>;

public sealed record CreateConversationCommand(CreateConversationRequest Request) : ICommand<ConversationDto>;

public sealed record DeleteConversationCommand(Guid Id) : ICommand<Unit>;

public sealed record GetConversationMessagesQuery(Guid ConversationId) : IQuery<IReadOnlyList<ChatMessageDto>>;

internal static class ChatMapping
{
    public static ConversationDto ToDto(this Conversation c) => new(c.Id, c.Title, c.AgentId, c.LastMessageAt, c.CreatedAt);

    /// <summary>
    /// 把存储的模型消息转换成界面消息：工具结果并入对应的工具调用，工具消息本身不单独展示。
    /// </summary>
    public static IReadOnlyList<ChatMessageDto> ToDtos(IReadOnlyList<ConversationMessage> rows, Func<string, string> actionKeyOf)
    {
        var results = new Dictionary<string, (JsonElement? Result, string? Status)>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.Role == ChatRole.Tool.Value))
        {
            var message = Deserialize(row);
            foreach (var result in message?.Contents.OfType<FunctionResultContent>() ?? [])
            {
                var element = result.Result is string s ? PlatformJson.Parse(s) : JsonSerializer.SerializeToElement(result.Result, AIJsonUtilities.DefaultOptions);
                var status = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("status", out var st) ? st.GetString() : null;
                results[result.CallId] = (element, status);
            }
        }

        var dtos = new List<ChatMessageDto>();
        foreach (var row in rows.Where(r => r.Role != ChatRole.Tool.Value && r.Role != ChatRole.System.Value))
        {
            var message = Deserialize(row);
            var calls = message?.Contents.OfType<FunctionCallContent>()
                .Select(c =>
                {
                    results.TryGetValue(c.CallId, out var r);
                    return new ToolCallDto(
                        c.CallId,
                        actionKeyOf(c.Name),
                        JsonSerializer.SerializeToElement(c.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions),
                        r.Result,
                        r.Status);
                })
                .ToList() ?? [];
            if (string.IsNullOrWhiteSpace(row.Text) && calls.Count == 0)
            {
                continue;
            }

            dtos.Add(new ChatMessageDto(row.Id, row.Seq, row.Role, row.Text, calls, row.CreatedAt));
        }

        return dtos;
    }

    public static ChatMessage? Deserialize(ConversationMessage row)
        => JsonSerializer.Deserialize<ChatMessage>(row.ContentJson, AIJsonUtilities.DefaultOptions);

    public static string Serialize(ChatMessage message) => JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions);
}

internal sealed class ConversationHandlers(IAppDbContext db, ITenantContext tenant, IActionInvoker actions, TimeProvider clock) :
    IRequestHandler<ListConversationsQuery, IReadOnlyList<ConversationDto>>,
    IRequestHandler<CreateConversationCommand, ConversationDto>,
    IRequestHandler<DeleteConversationCommand, Unit>,
    IRequestHandler<GetConversationMessagesQuery, IReadOnlyList<ChatMessageDto>>
{
    public async Task<IReadOnlyList<ConversationDto>> HandleAsync(ListConversationsQuery request, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        var rows = await db.Conversations.AsNoTracking().Where(c => c.UserId == userId).OrderByDescending(c => c.LastMessageAt).Take(100)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(c => c.ToDto())];
    }

    public async Task<ConversationDto> HandleAsync(CreateConversationCommand request, CancellationToken cancellationToken)
    {
        if (request.Request.AgentId is { } agentId && !await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("agent", agentId);
        }

        var conversation = new Conversation
        {
            UserId = CurrentUserId(),
            AgentId = request.Request.AgentId,
            Title = string.IsNullOrWhiteSpace(request.Request.Title) ? "新对话" : request.Request.Title.Trim(),
            LastMessageAt = clock.GetUtcNow(),
        };
        db.Conversations.Add(conversation);
        return conversation.ToDto();
    }

    public async Task<Unit> HandleAsync(DeleteConversationCommand request, CancellationToken cancellationToken)
    {
        var conversation = await FindOwnedAsync(request.Id, cancellationToken).ConfigureAwait(false);
        await db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Conversations.Remove(conversation);
        return Unit.Value;
    }

    public async Task<IReadOnlyList<ChatMessageDto>> HandleAsync(GetConversationMessagesQuery request, CancellationToken cancellationToken)
    {
        var conversation = await FindOwnedAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);
        var rows = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).OrderBy(m => m.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var keys = actions.Actions.ToDictionary(a => ActionToolset.ToToolName(a.Key), a => a.Key, StringComparer.Ordinal);
        return ChatMapping.ToDtos(rows, name => keys.GetValueOrDefault(name, name));
    }

    private async Task<Conversation> FindOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId();
        return await db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken).ConfigureAwait(false)
               ?? throw new NotFoundException("conversation", id);
    }

    private Guid CurrentUserId() => tenant.Actor is { IsUser: true, Id: { } id } ? id : throw new ForbiddenException("只有成员可以使用对话。");
}
