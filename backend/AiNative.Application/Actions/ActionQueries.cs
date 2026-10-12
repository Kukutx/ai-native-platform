using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Contracts.Actions;

namespace AiNative.Application.Actions;

/// <summary>
/// 当前身份可用的平台动作目录（界面的命令面板、MCP 工具列表都从这里取）。
/// </summary>
public sealed record ListActionsQuery : IQuery<IReadOnlyList<ActionDescriptorDto>>;

/// <summary>
/// 以当前身份调用平台动作。动作内部会分发自己的命令（各自带事务），这里不再包一层事务。
/// </summary>
public sealed record InvokeActionCommand(string Key, JsonElement Input) : IRequest<InvokeActionResponse>;

internal sealed class ActionQueryHandlers(IActionInvoker invoker) :
    IRequestHandler<ListActionsQuery, IReadOnlyList<ActionDescriptorDto>>,
    IRequestHandler<InvokeActionCommand, InvokeActionResponse>
{
    public async Task<IReadOnlyList<ActionDescriptorDto>> HandleAsync(ListActionsQuery request, CancellationToken cancellationToken)
    {
        var available = await invoker.GetAvailableAsync(cancellationToken).ConfigureAwait(false);
        return [.. available.Select(a => new ActionDescriptorDto(
            a.Key,
            a.ModuleKey,
            a.Title,
            a.Description,
            a.RequiredFeature,
            a.IsMutation,
            a.ApprovalPolicy == ApprovalPolicy.RequiredForAgents,
            invoker.GetInputSchema(a)))];
    }

    public async Task<InvokeActionResponse> HandleAsync(InvokeActionCommand request, CancellationToken cancellationToken)
    {
        var result = await invoker.InvokeAsync(request.Key, request.Input, new ActionInvocationOptions(), cancellationToken).ConfigureAwait(false);
        return new InvokeActionResponse(ToStatus(result.Status), result.Output, result.Error, result.ApprovalId);
    }

    private static string ToStatus(ActionInvocationStatus status) => status switch
    {
        ActionInvocationStatus.Succeeded => "succeeded",
        ActionInvocationStatus.PendingApproval => "pending_approval",
        ActionInvocationStatus.Failed => "failed",
        ActionInvocationStatus.Forbidden => "forbidden",
        ActionInvocationStatus.NotFound => "not_found",
        ActionInvocationStatus.InvalidInput => "invalid_input",
        _ => "failed",
    };
}
