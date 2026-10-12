using AiNative.Domain.Agents;
using AiNative.Domain.Analytics;
using AiNative.Domain.Chat;
using AiNative.Domain.Crm;
using AiNative.Domain.Erp;
using AiNative.Domain.Governance;
using AiNative.Domain.Identity;
using AiNative.Domain.Integrations;
using AiNative.Domain.Knowledge;
using AiNative.Domain.Modules;
using AiNative.Domain.Projects;
using AiNative.Domain.Tenancy;
using AiNative.Domain.Work;
using AiNative.Domain.Workflows;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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

    DbSet<KnowledgeDocument> KnowledgeDocuments { get; }

    DbSet<KnowledgeChunk> KnowledgeChunks { get; }

    DbSet<MemoryEntry> MemoryEntries { get; }

    DbSet<ApiKey> ApiKeys { get; }

    DbSet<Workflow> Workflows { get; }

    DbSet<WorkflowRun> WorkflowRuns { get; }

    DbSet<WorkflowRunStep> WorkflowRunSteps { get; }

    DbSet<CrmCompany> CrmCompanies { get; }

    DbSet<CrmContact> CrmContacts { get; }

    DbSet<CrmStage> CrmStages { get; }

    DbSet<CrmDeal> CrmDeals { get; }

    DbSet<CrmActivity> CrmActivities { get; }

    DbSet<CrmFieldDefinition> CrmFieldDefinitions { get; }

    DbSet<ErpProduct> ErpProducts { get; }

    DbSet<ErpWarehouse> ErpWarehouses { get; }

    DbSet<ErpStockLevel> ErpStockLevels { get; }

    DbSet<ErpStockMovement> ErpStockMovements { get; }

    DbSet<ErpSalesOrder> ErpSalesOrders { get; }

    DbSet<ErpPurchaseOrder> ErpPurchaseOrders { get; }

    DbSet<Project> Projects { get; }

    DbSet<ProjectMilestone> ProjectMilestones { get; }

    DbSet<ProjectMember> ProjectMembers { get; }

    DbSet<AnalyticsReport> AnalyticsReports { get; }

    DbSet<TenantSecret> TenantSecrets { get; }

    DbSet<McpServer> McpServers { get; }

    DatabaseFacade Database { get; }

    EntityEntry<TEntity> Entry<TEntity>(TEntity entity)
        where TEntity : class;

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 不区分大小写的包含匹配。应用层不依赖具体数据库：PostgreSQL 实现为 ILIKE，配合 pg_trgm 三元组索引，
/// 用户输入里的 % 和 _ 按字面匹配。
/// </summary>
public interface ITextMatcher
{
    /// <summary>
    /// 任意一个字段包含 <paramref name="term"/> 即匹配。
    /// </summary>
    IQueryable<T> WhereContains<T>(IQueryable<T> source, string term, params Expression<Func<T, string>>[] fields);
}

/// <summary>
/// 租户内按名称的编号序列（例如订单号），原子递增，随当前事务提交或回滚。
/// </summary>
public interface ISequenceAllocator
{
    Task<long> NextAsync(string name, CancellationToken cancellationToken = default);
}
