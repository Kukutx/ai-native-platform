using AiNative.Application.Abstractions;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Realtime;

namespace AiNative.Application.Realtime;

public static class RealtimeEntities
{
    public const string Agent = "agent";
    public const string WorkItem = "work_item";
    public const string Run = "run";
    public const string Approval = "approval";
    public const string Conversation = "conversation";
    public const string Module = "module";
}

public static class RealtimeChangeKind
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Deleted = "deleted";
}

/// <summary>
/// 向工作台推送的出口（SignalR 实现在 API 层）。推送是尽力而为的，失败不影响业务结果。
/// </summary>
public interface IRealtimeNotifier
{
    Task ChangedAsync(Guid tenantId, RealtimeChangeDto change, CancellationToken cancellationToken = default);

    Task RunEventAsync(Guid tenantId, RunEventDto runEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// 在当前租户下登记实体变化，事务提交后统一推送。
/// </summary>
public interface IChangeFeed
{
    void Publish(string entity, Guid id, string kind);
}

internal sealed class ChangeFeed(ITenantContext tenant, IPostCommitActions postCommit, IRealtimeNotifier notifier) : IChangeFeed
{
    public void Publish(string entity, Guid id, string kind)
    {
        var tenantId = tenant.TenantId;
        var change = new RealtimeChangeDto(entity, id, kind);
        postCommit.Enqueue(ct => notifier.ChangedAsync(tenantId, change, ct));
    }
}
