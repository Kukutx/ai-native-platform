using System.Text.Json;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Domain.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AiNative.Application.Actions;

/// <summary>
/// 把当前身份可用的平台动作转换成模型工具，并负责执行模型发起的工具调用。
/// 工具名只能包含字母、数字、下划线和连字符，因此 "tasks.create" 映射为 "tasks_create"。
/// </summary>
public sealed class ActionToolset
{
    private readonly Dictionary<string, IPlatformAction> _byToolName;

    private ActionToolset(IReadOnlyList<IPlatformAction> actions, IActionInvoker invoker)
    {
        _byToolName = actions.ToDictionary(a => ToToolName(a.Key), StringComparer.Ordinal);
        Tools = [.. actions.Select(a => (AITool)AIFunctionFactory.CreateDeclaration(ToToolName(a.Key), a.Description, invoker.GetInputSchema(a)))];
    }

    public IList<AITool> Tools { get; }

    public static async Task<ActionToolset> CreateAsync(IActionInvoker invoker, CancellationToken cancellationToken)
        => new(await invoker.GetAvailableAsync(cancellationToken).ConfigureAwait(false), invoker);

    public static string ToToolName(string actionKey) => actionKey.Replace('.', '_');

    public IPlatformAction? Resolve(string toolName) => _byToolName.GetValueOrDefault(toolName);

    /// <summary>
    /// 在独立作用域中以给定身份执行一次工具调用。
    /// </summary>
    public static Task<ActionInvocationResult> InvokeAsync(
        ITenantScopeFactory scopes,
        Guid tenantId,
        Actor actor,
        IEnumerable<string> features,
        string actionKey,
        IDictionary<string, object?>? arguments,
        ActionInvocationOptions options,
        CancellationToken cancellationToken)
    {
        var input = arguments is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(arguments, AIJsonUtilities.DefaultOptions);
        return scopes.RunAsync(tenantId, actor, features, services =>
            services.GetRequiredService<IActionInvoker>().InvokeAsync(actionKey, input, options, cancellationToken));
    }

    /// <summary>
    /// 回传给模型的工具结果：状态 + 输出或错误，审批中的给出明确说明。
    /// </summary>
    public static string FormatForModel(ActionInvocationResult result) => result.Status switch
    {
        ActionInvocationStatus.Succeeded => PlatformJson.Serialize(new { status = "ok", output = result.Output }),
        ActionInvocationStatus.PendingApproval => PlatformJson.Serialize(new
        {
            status = "pending_approval",
            approvalId = result.ApprovalId,
            message = "该操作需要人工审批，已提交审批单。不要重复提交；审批结果出来后你会被唤醒。",
        }),
        _ => PlatformJson.Serialize(new { status = result.Status.ToString().ToLowerInvariant(), error = result.Error }),
    };
}
