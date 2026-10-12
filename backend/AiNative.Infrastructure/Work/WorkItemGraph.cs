using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Work;

/// <summary>
/// 依赖图的递归查询：用 WITH RECURSIVE 沿依赖边走一遍，UNION 去重，即使数据异常出现环也会结束。
/// </summary>
internal sealed class WorkItemGraph(AppDbContext db, ITenantContext tenant) : IWorkItemGraph
{
    public async Task<bool> DependsOnAsync(Guid workItemId, Guid dependsOnId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        var found = await db.Database.SqlQuery<bool>($"""
            WITH RECURSIVE upstream(id) AS (
                SELECT depends_on_id FROM work_item_dependencies WHERE tenant_id = {tenantId} AND work_item_id = {workItemId}
                UNION
                SELECT d.depends_on_id FROM work_item_dependencies d JOIN upstream u ON d.work_item_id = u.id WHERE d.tenant_id = {tenantId}
            )
            SELECT EXISTS (SELECT 1 FROM upstream WHERE id = {dependsOnId}) AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return found[0];
    }

    public async Task<IReadOnlyList<ScheduleEdge>> DownstreamAsync(Guid rootId, CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        var rows = await db.Database.SqlQuery<EdgeRow>($"""
            WITH RECURSIVE downstream(id) AS (
                SELECT {rootId}
                UNION
                SELECT d.work_item_id FROM work_item_dependencies d JOIN downstream s ON d.depends_on_id = s.id WHERE d.tenant_id = {tenantId}
            )
            SELECT d.work_item_id AS successor, d.depends_on_id AS predecessor
            FROM work_item_dependencies d
            WHERE d.tenant_id = {tenantId}
              AND d.work_item_id IN (SELECT id FROM downstream)
              AND d.depends_on_id IN (SELECT id FROM downstream)
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new ScheduleEdge(r.Successor, r.Predecessor))];
    }

    private sealed record EdgeRow(Guid Successor, Guid Predecessor);
}
