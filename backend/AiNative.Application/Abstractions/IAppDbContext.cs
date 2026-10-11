using AiNative.Domain.Agents;
using AiNative.Domain.Chat;
using AiNative.Domain.Governance;
using AiNative.Domain.Identity;
using AiNative.Domain.Modules;
using AiNative.Domain.Tenancy;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AiNative.Application.Abstractions;

/// <summary>
/// 应用层看到的数据库。租户隔离由全局查询过滤器自动完成，处理器里不需要手写 TenantId 条件。
/// </summary>
public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Organization> Organizations { get; }

    DbSet<User> Users { get; }

    DbSet<Membership> Memberships { get; }

    DbSet<Role> Roles { get; }

    DbSet<TenantModule> TenantModules { get; }

    DbSet<Agent> Agents { get; }

    DbSet<WakeupRequest> WakeupRequests { get; }

    DbSet<HeartbeatRun> HeartbeatRuns { get; }

    DbSet<RunEvent> RunEvents { get; }

    DbSet<WorkItem> WorkItems { get; }

    DbSet<WorkItemComment> WorkItemComments { get; }

    DbSet<Approval> Approvals { get; }

    DbSet<CostEvent> CostEvents { get; }

    DbSet<Conversation> Conversations { get; }

    DbSet<ConversationMessage> ConversationMessages { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
