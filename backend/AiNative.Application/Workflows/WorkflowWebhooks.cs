using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Application.Workflows.Expressions;
using AiNative.Contracts.Workflows;
using AiNative.Domain.Common;
using AiNative.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiNative.Application.Workflows;

/// <summary>
/// 外部系统通过 Webhook 地址触发工作流。不需要登录：地址里的令牌就是凭证，所以只按令牌找到工作流，
/// 再进入它所在的空间，以系统身份创建运行（步骤仍以工作流所有者的身份和权限执行）。
/// </summary>
/// <param name="Body">请求体：JSON 解析后的值、表单字段，或纯文本。</param>
/// <param name="Headers">请求头，名称小写，已去掉 Authorization、Cookie 等凭证；其余敏感请求头只用于过滤条件，保存时打码。</param>
public sealed record ReceiveWorkflowWebhookCommand(
    string Token,
    JsonElement? Body,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers) : ICommand<WebhookAcceptedDto>;

internal static class WorkflowWebhooks
{
    public const int TokenMaxLength = 64;

    /// <summary>
    /// 32 字节随机数的 base64url，43 个字符。
    /// </summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
}

internal sealed class WorkflowWebhookHandler(
    IAppDbContext db,
    TenantContext tenant,
    IModuleAccess modules,
    WorkflowExecutionScope scope,
    ITransactionLock locks,
    IPostCommitActions postCommit,
    IWorkflowSignal signal,
    IChangeFeed changes,
    IOptions<WorkflowOptions> options,
    TimeProvider clock,
    ILogger<WorkflowWebhookHandler> logger) : IRequestHandler<ReceiveWorkflowWebhookCommand, WebhookAcceptedDto>
{
    public async Task<WebhookAcceptedDto> HandleAsync(ReceiveWorkflowWebhookCommand request, CancellationToken cancellationToken)
    {
        var token = request.Token;
        var target = token.Length is > 0 and <= WorkflowWebhooks.TokenMaxLength
            ? await db.Workflows.IgnoreQueryFilters().AsNoTracking()
                .Where(w => w.WebhookToken == token)
                .Select(w => new { w.Id, w.TenantId })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (target is null)
        {
            throw NotFound();
        }

        tenant.Enter(target.TenantId, Actor.System, []);
        if (!await modules.IsEnabledAsync(WorkflowsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            throw NotFound();
        }

        // 与事件触发、删除工作流持有同一把锁：频率计数和新建运行是原子的，也不会给刚删除的工作流插入运行。
        await locks.AcquireAsync(WorkflowTriggers.TriggerLockKey(target.Id), cancellationToken).ConfigureAwait(false);
        var workflow = await db.Workflows.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == target.Id && w.IsEnabled && w.TriggerKind == WorkflowTriggerKind.Webhook && w.WebhookToken == token, cancellationToken)
            .ConfigureAwait(false)
            ?? throw NotFound();

        var now = clock.GetUtcNow();
        var data = PlatformJson.ToElement(new { body = request.Body, query = request.Query, headers = request.Headers, receivedAt = now });
        if (!Matches(workflow, data))
        {
            return new WebhookAcceptedDto(false, null);
        }

        // 过滤条件可以核对 x-gitlab-token 这类校验头，但运行记录对所有能查看工作流的人可见，保存前打码。
        var masked = request.Headers.ToDictionary(
            h => h.Key,
            h => SensitiveHeaders.IsSensitive(h.Key) ? HttpStepSecurity.Masked : h.Value,
            StringComparer.Ordinal);
        data = PlatformJson.ToElement(new { body = request.Body, query = request.Query, headers = masked, receivedAt = now });

        var since = now.AddMinutes(-1);
        var recent = await db.WorkflowRuns.CountAsync(r => r.WorkflowId == workflow.Id && r.TriggerKind == WorkflowTriggerKind.Webhook && r.CreatedAt > since, cancellationToken)
            .ConfigureAwait(false);
        if (recent >= options.Value.MaxEventRunsPerMinute)
        {
            throw new RateLimitedException("workflow.rate_limited", $"这个工作流每分钟最多由 Webhook 触发 {options.Value.MaxEventRunsPerMinute} 次，请稍后再试。");
        }

        var run = WorkflowRuns.Create(workflow, WorkflowTriggerKind.Webhook, null, Actor.System, data, default, scope);
        db.WorkflowRuns.Add(run);
        changes.Publish(RealtimeEntities.WorkflowRun, run.Id, RealtimeChangeKind.Created);
        postCommit.Enqueue(_ =>
        {
            signal.Notify();
            return Task.CompletedTask;
        });
        return new WebhookAcceptedDto(true, run.Id);
    }

    private bool Matches(Workflow workflow, JsonElement data)
    {
        if (string.IsNullOrWhiteSpace(workflow.EventFilter))
        {
            return true;
        }

        try
        {
            return WorkflowExpression.Parse(workflow.EventFilter).IsTrue(PlatformJson.ToElement(new { trigger = data }));
        }
        catch (WorkflowExpressionException ex)
        {
            logger.LogWarning("Workflow {WorkflowId} has an invalid webhook filter: {Message}", workflow.Id, ex.Message);
            return false;
        }
    }

    private static NotFoundException NotFound() => new("workflow_webhook", string.Empty, "Webhook 地址无效，或对应的工作流已停用。");
}
