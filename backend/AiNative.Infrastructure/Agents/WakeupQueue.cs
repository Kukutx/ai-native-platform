using AiNative.Application.Abstractions;
using AiNative.Application.Agents;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Common;
using AiNative.Infrastructure.Dispatching;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Agents;

/// <summary>
/// 唤醒队列：一条 SQL 完成"没有排队请求就新建，有就把原因追加进去"，同一智能体永远最多一条排队中的唤醒。
/// 合并的原因只保留最近 <see cref="WakeupRequest.MaxItems"/> 条，智能体长时间暂停也不会让提示词无限膨胀；
/// 合并次数照常累计，提示词里会说明省略了多少条。
/// </summary>
internal sealed class WakeupQueue(
    AppDbContext db,
    ITenantContext tenant,
    PostCommitActions postCommit,
    IWakeupSignal signal,
    IChangeFeed changes,
    TimeProvider clock) : IWakeupQueue
{
    public async Task<Guid> EnqueueAsync(
        Guid agentId,
        WakeupItem item,
        Actor requestedBy,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return await EnqueueInTransactionAsync(agentId, item, requestedBy, idempotencyKey, cancellationToken).ConfigureAwait(false);
        }

        // 不在事务里（例如审批决定之后的通知）时自己开一个：事务级锁只有在事务里才能把幂等检查和合并串起来，
        // 唤醒信号也要等提交之后再发，调度器才能看到这条请求。
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        postCommit.Begin();
        try
        {
            var id = await EnqueueInTransactionAsync(agentId, item, requestedBy, idempotencyKey, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await postCommit.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        catch
        {
            postCommit.Abort();
            throw;
        }
    }

    private async Task<Guid> EnqueueInTransactionAsync(
        Guid agentId,
        WakeupItem item,
        Actor requestedBy,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var now = clock.GetUtcNow();

        // 同一智能体的入队在事务内串行化，幂等检查与合并之间没有竞态。
        var lockKey = $"wakeup:{agentId}";
        var maxItems = WakeupRequest.MaxItems;
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken)
            .ConfigureAwait(false);

        if (idempotencyKey is not null)
        {
            var existing = await db.WakeupRequests.AsNoTracking()
                .Where(w => w.AgentId == agentId && w.IdempotencyKey == idempotencyKey)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (existing is { } existingId)
            {
                return existingId;
            }
        }

        var id = Guid.CreateVersion7();
        var items = PlatformJson.Serialize(new[] { item.RequestedAt == default ? item with { RequestedAt = now } : item });
        var actorKind = requestedBy.Kind.ToString();
        var actorId = requestedBy.Id;

        // 合并进已有排队请求时，幂等键只在对方没有键时继承（同一批次里第二个键不再单独记录）。
        var ids = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO wakeup_requests
                (id, tenant_id, agent_id, status, items, coalesced_count, idempotency_key, requested_by_kind, requested_by_id, created_at, updated_at)
            VALUES
                ({id}, {tenantId}, {agentId}, 'Queued', CAST({items} AS jsonb), 0, {idempotencyKey}, {actorKind}, {actorId}, {now}, {now})
            ON CONFLICT (agent_id) WHERE status = 'Queued'
            DO UPDATE SET
                items = (
                    SELECT COALESCE(jsonb_agg(e.value ORDER BY e.ordinality), '[]'::jsonb)
                    FROM (
                        SELECT value, ordinality
                        FROM jsonb_array_elements(wakeup_requests.items || EXCLUDED.items) WITH ORDINALITY
                        ORDER BY ordinality DESC
                        LIMIT {maxItems}
                    ) AS e),
                coalesced_count = wakeup_requests.coalesced_count + 1,
                idempotency_key = COALESCE(wakeup_requests.idempotency_key, EXCLUDED.idempotency_key),
                updated_at = EXCLUDED.updated_at
            RETURNING id AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        postCommit.Enqueue(_ =>
        {
            signal.Notify();
            return Task.CompletedTask;
        });
        changes.Publish(RealtimeEntities.Agent, agentId, RealtimeChangeKind.Updated);
        return ids[0];
    }
}
