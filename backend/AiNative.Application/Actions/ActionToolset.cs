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
    /// 回传给模型的工具结果的字符数上限（约 8 千到 3 万个 token）。超过时只给开头一段并提示缩小范围，
    /// 避免一次查询把上下文撑满、费用失控。
    /// </summary>
    public const int MaxModelOutputChars = 32_000;

    /// <summary>
    /// 回传给模型的工具结果：状态 + 输出或错误，审批中的给出明确说明。结果总是合法的 JSON，超长时截断。
    /// </summary>
    public static string FormatForModel(ActionInvocationResult result)
    {
        var formatted = Format(result);
        if (formatted.Length <= MaxModelOutputChars)
        {
            return formatted;
        }

        // 预览放进字符串后引号等会被转义、变长，按实际长度收缩到上限以内。
        var status = result.Status == ActionInvocationStatus.Succeeded ? "ok" : result.Status.ToString().ToLowerInvariant();
        var cut = MaxModelOutputChars;
        while (true)
        {
            if (cut > 0 && char.IsHighSurrogate(formatted[cut - 1]))
            {
                cut--;
            }

            var text = PlatformJson.Serialize(new
            {
                status,
                truncated = true,
                preview = formatted[..cut],
                message = $"结果有 {formatted.Length} 个字符，超过上限，只给出了开头 {cut} 个字符。需要完整信息时请缩小范围（加筛选条件、减小每页条数或分页查询）后再调用。",
            });
            if (text.Length <= MaxModelOutputChars || cut == 0)
            {
                return text;
            }

            cut = Math.Max(0, cut - (text.Length - MaxModelOutputChars) - 16);
        }
    }

    private static string Format(ActionInvocationResult result) => result.Status switch
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
