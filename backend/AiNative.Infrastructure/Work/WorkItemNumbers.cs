using AiNative.Application.Tenancy;
using AiNative.Application.Work;
using AiNative.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Infrastructure.Work;

/// <summary>
/// 租户内任务编号（T-1、T-2…）：在租户行上原子自增，随当前事务提交或回滚。
/// </summary>
internal sealed class WorkItemNumbers(AppDbContext db, ITenantContext tenant) : IWorkItemNumbers
{
    public async Task<int> NextAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = tenant.TenantId;
        var values = await db.Database.SqlQuery<int>($"""
            UPDATE tenants SET work_item_sequence = work_item_sequence + 1
            WHERE id = {tenantId}
            RETURNING work_item_sequence AS "Value"
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return values.Count == 1 ? values[0] : throw new InvalidOperationException($"Tenant {tenantId} not found.");
    }
}
