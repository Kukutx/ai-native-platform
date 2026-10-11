using System.Collections.Concurrent;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Actions;

public enum ActionInvocationStatus
{
    Succeeded = 0,
    PendingApproval = 1,
    Failed = 2,
    Forbidden = 3,
    NotFound = 4,
    InvalidInput = 5,
}

public sealed record ActionInvocationResult(
    ActionInvocationStatus Status,
    JsonElement? Output = null,
    string? Error = null,
    Guid? ApprovalId = null)
{
    public bool IsSuccess => Status == ActionInvocationStatus.Succeeded;
}

public sealed record ActionInvocationOptions
{
    public Guid? RunId { get; init; }

    public Guid? WorkItemId { get; init; }

    /// <summary>
    /// 发起方给出的理由，进入审批单。
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// 审批通过后的执行：不再二次进入审批。
    /// </summary>
    public Guid? ApprovedBy { get; init; }

    /// <summary>
    /// 调用方额外要求审批的动作键（例如智能体自身配置的审批清单）。
    /// </summary>
    public IReadOnlyCollection<string> ExtraApprovalRequired { get; init; } = [];
}

public interface IActionInvoker
{
    IReadOnlyList<IPlatformAction> Actions { get; }

    IPlatformAction? Find(string key);

    /// <summary>
    /// 当前身份可用的动作：所属模块已启用，且拥有所需权限。
    /// </summary>
    Task<IReadOnlyList<IPlatformAction>> GetAvailableAsync(CancellationToken cancellationToken = default);

    JsonElement GetInputSchema(IPlatformAction action);

    Task<ActionInvocationResult> InvokeAsync(
        string key,
        JsonElement input,
        ActionInvocationOptions options,
        CancellationToken cancellationToken = default);
}

internal sealed class ActionInvoker(
    IEnumerable<IPlatformAction> actions,
    IServiceProvider services,
    ITenantContext tenant,
    IModuleAccess modules,
    IAppDbContext db,
    IChangeFeed changes,
    ILogger<ActionInvoker> logger) : IActionInvoker
{
    private static readonly ConcurrentDictionary<Type, JsonElement> SchemaCache = new();

    private readonly IReadOnlyList<IPlatformAction> _actions = [.. actions.OrderBy(a => a.Key, StringComparer.Ordinal)];

    public IReadOnlyList<IPlatformAction> Actions => _actions;

    public IPlatformAction? Find(string key) => _actions.FirstOrDefault(a => a.Key == key);

    public async Task<IReadOnlyList<IPlatformAction>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        return [.. _actions.Where(a => enabled.Contains(a.ModuleKey) && tenant.Can(a.RequiredFeature))];
    }

    public JsonElement GetInputSchema(IPlatformAction action)
        => SchemaCache.GetOrAdd(action.InputType, static type =>
            AIJsonUtilities.CreateJsonSchema(type, serializerOptions: PlatformJson.Options));

    public async Task<ActionInvocationResult> InvokeAsync(
        string key,
        JsonElement input,
        ActionInvocationOptions options,
        CancellationToken cancellationToken = default)
    {
        var action = Find(key);
        if (action is null || !await modules.IsEnabledAsync(action.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return new ActionInvocationResult(ActionInvocationStatus.NotFound, Error: $"动作 {key} 不存在或所属模块未启用。");
        }

        if (!tenant.Can(action.RequiredFeature))
        {
            return new ActionInvocationResult(ActionInvocationStatus.Forbidden, Error: $"缺少权限 {action.RequiredFeature}。");
        }

        object? typedInput;
        try
        {
            typedInput = input.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? JsonSerializer.Deserialize("{}", action.InputType, PlatformJson.Options)
                : input.Deserialize(action.InputType, PlatformJson.Options);
        }
        catch (JsonException ex)
        {
            return new ActionInvocationResult(ActionInvocationStatus.InvalidInput, Error: $"输入不合法：{ex.Message}");
        }

        if (typedInput is null)
        {
            return new ActionInvocationResult(ActionInvocationStatus.InvalidInput, Error: "输入不能为空。");
        }

        var validationError = await ValidateAsync(action.InputType, typedInput, cancellationToken).ConfigureAwait(false);
        if (validationError is not null)
        {
            return new ActionInvocationResult(ActionInvocationStatus.InvalidInput, Error: validationError);
        }

        if (options.ApprovedBy is null && RequiresApproval(action, options))
        {
            var approval = new Approval
            {
                ActionKey = action.Key,
                InputJson = JsonSerializer.Serialize(typedInput, action.InputType, PlatformJson.Options),
                Rationale = options.Rationale,
                RequestedBy = tenant.Actor,
                RunId = options.RunId,
                WorkItemId = options.WorkItemId,
            };
            db.Approvals.Add(approval);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            changes.Publish(RealtimeEntities.Approval, approval.Id, RealtimeChangeKind.Created);
            return new ActionInvocationResult(ActionInvocationStatus.PendingApproval, ApprovalId: approval.Id);
        }

        try
        {
            var context = new ActionContext(tenant.Actor, options.RunId, WorkItemId: options.WorkItemId);
            var output = await action.ExecuteAsync(typedInput, context, cancellationToken).ConfigureAwait(false);
            return new ActionInvocationResult(ActionInvocationStatus.Succeeded, PlatformJson.ToElement(output));
        }
        catch (ValidationFailedException ex)
        {
            return new ActionInvocationResult(ActionInvocationStatus.InvalidInput, Error: FormatErrors(ex.Errors));
        }
        catch (ForbiddenException ex)
        {
            return new ActionInvocationResult(ActionInvocationStatus.Forbidden, Error: ex.Message);
        }
        catch (NotFoundException ex)
        {
            return new ActionInvocationResult(ActionInvocationStatus.NotFound, Error: ex.Message);
        }
        catch (Exception ex) when (ex is AppException or DomainException)
        {
            logger.LogInformation("Action {ActionKey} rejected: {Message}", key, ex.Message);
            return new ActionInvocationResult(ActionInvocationStatus.Failed, Error: ex.Message);
        }
    }

    private bool RequiresApproval(IPlatformAction action, ActionInvocationOptions options)
    {
        if (!tenant.Actor.IsAgent)
        {
            return false;
        }

        return action.ApprovalPolicy == ApprovalPolicy.RequiredForAgents
               || options.ExtraApprovalRequired.Contains(action.Key, StringComparer.Ordinal);
    }

    private async Task<string?> ValidateAsync(Type inputType, object input, CancellationToken cancellationToken)
    {
        var validatorType = typeof(IValidator<>).MakeGenericType(inputType);
        if (services.GetService(validatorType) is not IValidator validator)
        {
            return null;
        }

        var context = new ValidationContext<object>(input);
        var result = await validator.ValidateAsync(context, cancellationToken).ConfigureAwait(false);
        return result.IsValid ? null : string.Join("；", result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}"));
    }

    private static string FormatErrors(IReadOnlyDictionary<string, string[]> errors)
        => string.Join("；", errors.Select(kv => $"{kv.Key}: {string.Join("，", kv.Value)}"));
}
