using AiNative.Application.Tenancy;
using AiNative.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiNative.Infrastructure.Persistence;

/// <summary>
/// 保存前统一写入时间戳，并为新增的租户数据补上当前租户；发现跨租户写入直接拒绝。
/// </summary>
internal sealed class AuditingInterceptor(ITenantContext tenant, TimeProvider clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }

                if (entry.Entity.UpdatedAt == default)
                {
                    entry.Entity.UpdatedAt = now;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                // 处理器显式写入了新的 UpdatedAt（例如作为并发版本返回给客户端）时保留它，返回的 DTO 与落库值一致；
                // 其余情况（包括只把 UpdatedAt 标记为已修改来"刷新时间"）统一写当前时间。
                var updatedAt = entry.Property(e => e.UpdatedAt);
                if (updatedAt.CurrentValue == updatedAt.OriginalValue)
                {
                    entry.Entity.UpdatedAt = now;
                }
            }

            if (entry.Entity is not ITenantScoped scoped || entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (entry.State == EntityState.Added && scoped.TenantId == Guid.Empty)
            {
                scoped.TenantId = tenant.TenantId;
            }
            else if (tenant.HasTenant && scoped.TenantId != tenant.TenantId)
            {
                throw new InvalidOperationException($"拒绝跨租户写入：{entry.Metadata.ClrType.Name} {entry.Entity.Id}。");
            }
        }
    }
}
