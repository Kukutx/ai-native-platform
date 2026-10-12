using System.Linq.Expressions;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Analytics;
using AiNative.Domain.Chat;
using AiNative.Domain.Common;
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
using AiNative.Infrastructure.Cluster;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace AiNative.Infrastructure.Persistence;

/// <summary>
/// 平台数据库。所有实现 ITenantScoped 的实体自动带租户过滤器；表名、列名统一 snake_case，枚举存字符串。
/// </summary>
public sealed class AppDbContext : DbContext, IAppDbContext
{
    private readonly ITenantContext tenant;
    private readonly TimeProvider clock;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant, TimeProvider clock)
        : base(options)
    {
        this.tenant = tenant;
        this.clock = clock;
        ChangeTracker.Tracked += StampNewEntity;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<User> Users => Set<User>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<TenantModule> TenantModules => Set<TenantModule>();

    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<WakeupRequest> WakeupRequests => Set<WakeupRequest>();

    public DbSet<HeartbeatRun> HeartbeatRuns => Set<HeartbeatRun>();

    public DbSet<RunEvent> RunEvents => Set<RunEvent>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<WorkItemComment> WorkItemComments => Set<WorkItemComment>();

    public DbSet<WorkItemDependency> WorkItemDependencies => Set<WorkItemDependency>();

    internal DbSet<ClusterInstance> ClusterInstances => Set<ClusterInstance>();

    internal DbSet<ClusterMessage> ClusterMessages => Set<ClusterMessage>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<CostEvent> CostEvents => Set<CostEvent>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<MemoryEntry> MemoryEntries => Set<MemoryEntry>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();

    public DbSet<WorkflowRunStep> WorkflowRunSteps => Set<WorkflowRunStep>();

    public DbSet<CrmCompany> CrmCompanies => Set<CrmCompany>();

    public DbSet<CrmContact> CrmContacts => Set<CrmContact>();

    public DbSet<CrmStage> CrmStages => Set<CrmStage>();

    public DbSet<CrmDeal> CrmDeals => Set<CrmDeal>();

    public DbSet<CrmActivity> CrmActivities => Set<CrmActivity>();

    public DbSet<CrmFieldDefinition> CrmFieldDefinitions => Set<CrmFieldDefinition>();

    public DbSet<ErpProduct> ErpProducts => Set<ErpProduct>();

    public DbSet<ErpWarehouse> ErpWarehouses => Set<ErpWarehouse>();

    public DbSet<ErpStockLevel> ErpStockLevels => Set<ErpStockLevel>();

    public DbSet<ErpStockMovement> ErpStockMovements => Set<ErpStockMovement>();

    public DbSet<ErpSalesOrder> ErpSalesOrders => Set<ErpSalesOrder>();

    public DbSet<ErpPurchaseOrder> ErpPurchaseOrders => Set<ErpPurchaseOrder>();

    public DbSet<ErpStockDocument> ErpStockDocuments => Set<ErpStockDocument>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectMilestone> ProjectMilestones => Set<ProjectMilestone>();

    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();

    public DbSet<AnalyticsReport> AnalyticsReports => Set<AnalyticsReport>();

    public DbSet<TenantSecret> TenantSecrets => Set<TenantSecret>();

    public DbSet<McpServer> McpServers => Set<McpServer>();

    /// <summary>
    /// 查询过滤器引用的当前租户；EF 按上下文实例参数化，未进入租户时为空 Guid，什么都查不到。
    /// </summary>
    internal Guid CurrentTenantId => tenant.TenantIdOrEmpty;

    /// <summary>
    /// 新实体加入跟踪时就写入时间戳和租户，处理器在保存前返回的 DTO 也能拿到真实的 CreatedAt；保存时由 AuditingInterceptor 兜底并校验。
    /// </summary>
    private void StampNewEntity(object? sender, EntityTrackedEventArgs e)
    {
        if (e.FromQuery || e.Entry.State != EntityState.Added || e.Entry.Entity is not Entity entity)
        {
            return;
        }

        var now = clock.GetUtcNow();
        if (entity.CreatedAt == default)
        {
            entity.CreatedAt = now;
        }

        entity.UpdatedAt = now;
        if (entity is ITenantScoped scoped && scoped.TenantId == Guid.Empty && tenant.HasTenant)
        {
            scoped.TenantId = tenant.TenantId;
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        foreach (var enumType in typeof(Entity).Assembly.GetTypes().Where(t => t.IsEnum))
        {
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(32);
        }

        configurationBuilder.Properties<decimal>().HavePrecision(18, 6);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 向量检索（knowledge_chunks.embedding）依赖 pgvector 扩展；客户管理的模糊搜索用 pg_trgm 的三元组索引。
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Tenant>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Slug).HasMaxLength(64);
            b.Property(x => x.TimeZone).HasMaxLength(64).HasDefaultValue("UTC");
            b.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Organization>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Path).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.Path });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.Property(x => x.Email).HasMaxLength(254);
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Membership>(b =>
        {
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.Property(x => x.Key).HasMaxLength(50);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantModule>(b =>
        {
            b.Property(x => x.ModuleKey).HasMaxLength(64);
            b.HasIndex(x => new { x.TenantId, x.ModuleKey }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Title).HasMaxLength(100);
            b.Property(x => x.ModelProvider).HasMaxLength(64);
            b.Property(x => x.Model).HasMaxLength(128);
            b.Property(x => x.PauseReason).HasMaxLength(500);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.ReportsToId });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.ReportsToId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WakeupRequest>(b =>
        {
            b.ComplexProperty(x => x.RequestedBy);
            b.Property(x => x.Items)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, PlatformJson.Options),
                    v => JsonSerializer.Deserialize<List<WakeupItem>>(v, PlatformJson.Options) ?? new List<WakeupItem>(),
                    new ValueComparer<List<WakeupItem>>(
                        (a, c) => JsonSerializer.Serialize(a, PlatformJson.Options) == JsonSerializer.Serialize(c, PlatformJson.Options),
                        v => JsonSerializer.Serialize(v, PlatformJson.Options).GetHashCode(StringComparison.Ordinal),
                        v => JsonSerializer.Deserialize<List<WakeupItem>>(JsonSerializer.Serialize(v, PlatformJson.Options), PlatformJson.Options)!));
            b.Property(x => x.IdempotencyKey).HasMaxLength(200);

            // 每个智能体最多一条排队中的唤醒：新唤醒通过 ON CONFLICT 合并进去。
            b.HasIndex(x => x.AgentId).IsUnique().HasFilter("status = 'Queued'").HasDatabaseName("ux_wakeup_requests_agent_queued");
            b.HasIndex(x => new { x.AgentId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL");
            b.HasIndex(x => new { x.Status, x.CreatedAt });
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HeartbeatRun>(b =>
        {
            b.Property(x => x.Provider).HasMaxLength(64);
            b.Property(x => x.Model).HasMaxLength(128);
            b.Property(x => x.Error).HasMaxLength(4000);

            // 同一智能体同一时刻只能有一轮在执行，多实例部署时也成立。
            b.HasIndex(x => x.AgentId).IsUnique().HasFilter("status IN ('Queued', 'Running')").HasDatabaseName("ux_heartbeat_runs_agent_active");
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.AgentId, x.CreatedAt });
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RunEvent>(b =>
        {
            b.Property(x => x.DataJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.RunId, x.Seq }).IsUnique();
            b.HasOne<HeartbeatRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItem>(b =>
        {
            b.Property(x => x.Title).HasMaxLength(300);
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.AssigneeAgentId, x.Status });
            b.HasIndex(x => x.ParentId);
            b.HasIndex(x => x.CheckoutRunId);
            b.HasIndex(x => new { x.ProjectId, x.Status });
            b.HasIndex(x => x.MilestoneId);

            // 经营分析按创建时间、完成时间分天统计。
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CompletedAt }).HasFilter("completed_at IS NOT NULL");
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AssigneeAgentId).OnDelete(DeleteBehavior.SetNull);

            // 删除项目或里程碑时任务保留，只是不再归属。
            b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<ProjectMilestone>().WithMany().HasForeignKey(x => x.MilestoneId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Project>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(10_000);
            b.ComplexProperty(x => x.CreatedBy);

            // 名称在空间内唯一（不区分大小写，处理器在事务锁下检查），智能体可以按名称指代项目。
            b.HasIndex(x => new { x.TenantId, x.Status });
            b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.LeadAgentId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.LeadUserId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.LeadAgentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ProjectMilestone>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(2_000);
            b.HasIndex(x => new { x.ProjectId, x.DueDate });
            b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectMember>(b =>
        {
            b.Property(x => x.Responsibility).HasMaxLength(100);

            // 同一个人或智能体在一个项目里只出现一次；成员与智能体二选一。
            b.HasIndex(x => new { x.ProjectId, x.UserId }).IsUnique().HasFilter("user_id IS NOT NULL");
            b.HasIndex(x => new { x.ProjectId, x.AgentId }).IsUnique().HasFilter("agent_id IS NOT NULL");
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => x.AgentId);
            b.ToTable(t => t.HasCheckConstraint("ck_project_members_one_member", "(user_id IS NULL) <> (agent_id IS NULL)"));
            b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AnalyticsReport>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(1_000);
            b.Property(x => x.Metric).HasMaxLength(64);
            b.Property(x => x.Breakdown).HasMaxLength(32);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => new { x.TenantId, x.Position });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantSecret>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(64);
            b.Property(x => x.Description).HasMaxLength(500);
            b.ComplexProperty(x => x.CreatedBy);
            b.ComplexProperty(x => x.UpdatedBy);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<McpServer>(b =>
        {
            b.Property(x => x.Key).HasMaxLength(20);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(1_000);
            b.Property(x => x.Url).HasMaxLength(2_000);
            b.Property(x => x.LastError).HasMaxLength(2_000);
            b.ComplexProperty(x => x.CreatedBy);
            JsonList(b.Property(x => x.Headers));
            JsonList(b.Property(x => x.DisabledTools));
            JsonList(b.Property(x => x.ReadOnlyTools));
            JsonList(b.Property(x => x.Tools));
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemDependency>(b =>
        {
            // 同一对任务只有一条依赖；按前置任务查后续任务（关闭时唤醒、顺延）走第二个索引。
            b.HasIndex(x => new { x.WorkItemId, x.DependsOnId }).IsUnique();
            b.HasIndex(x => x.DependsOnId);
            b.ToTable(t => t.HasCheckConstraint("ck_work_item_dependencies_not_self", "work_item_id <> depends_on_id"));
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.DependsOnId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkItemComment>(b =>
        {
            b.ComplexProperty(x => x.Author);
            b.HasIndex(x => new { x.WorkItemId, x.CreatedAt });
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Approval>(b =>
        {
            b.Property(x => x.ActionKey).HasMaxLength(100);
            b.Property(x => x.InputJson).HasColumnType("jsonb");
            b.Property(x => x.ResultJson).HasColumnType("jsonb");
            b.Property(x => x.ExecutionError).HasMaxLength(4000);
            b.Property(x => x.DecisionNote).HasMaxLength(2000);
            b.ComplexProperty(x => x.RequestedBy);
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });

            // 已批准但结果没写回的审批：调度器定期据此找出执行被中断的。
            b.HasIndex(x => x.DecidedAt).HasFilter("status = 'Approved' AND result_json IS NULL AND execution_error IS NULL").HasDatabaseName("ix_approvals_unrecorded");
        });

        modelBuilder.Entity<CostEvent>(b =>
        {
            b.Property(x => x.Provider).HasMaxLength(64);
            b.Property(x => x.Model).HasMaxLength(128);
            b.ComplexProperty(x => x.Actor);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasIndex(x => x.RunId);
        });

        modelBuilder.Entity<Conversation>(b =>
        {
            b.Property(x => x.Title).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.UserId, x.LastMessageAt });
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationMessage>(b =>
        {
            b.Property(x => x.Role).HasMaxLength(32);
            b.Property(x => x.ContentJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.ConversationId, x.Seq }).IsUnique();
            b.HasOne<Conversation>().WithMany().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Prefix).HasMaxLength(32);
            b.Property(x => x.SecretHash).HasMaxLength(64);
            b.HasIndex(x => x.Prefix).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Membership>().WithMany().HasForeignKey(x => x.MembershipId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeDocument>(b =>
        {
            b.Property(x => x.Title).HasMaxLength(300);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.Property(x => x.FileName).HasMaxLength(260);
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryEntry>(b =>
        {
            b.Property(x => x.Content).HasMaxLength(4000);
            b.Property(x => x.ContentHash).HasMaxLength(64);
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => new { x.TenantId, x.Scope, x.OwnerId, x.ContentHash }).IsUnique().AreNullsDistinct(false);
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeChunk>(b =>
        {
            b.Property(x => x.EmbeddingModel).HasMaxLength(200);

            // 关键词检索：分词结果直接作为词位（array_to_tsvector 不再做任何解析），GIN 索引。
            b.Property<NpgsqlTypes.NpgsqlTsVector>("SearchVector")
                .HasComputedColumnSql("array_to_tsvector(string_to_array(search_text, ' '))", stored: true);
            b.HasIndex("SearchVector").HasMethod("gin");

            b.HasIndex(x => new { x.TenantId, x.SourceKind, x.Scope, x.OwnerId });
            b.HasIndex(x => new { x.DocumentId, x.Seq });
            b.HasIndex(x => x.MemoryId);
            b.HasIndex(x => x.CreatedAt).HasFilter("embedding_model IS NULL").HasDatabaseName("ix_knowledge_chunks_pending_embedding");
            b.HasOne<KnowledgeDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<MemoryEntry>().WithMany().HasForeignKey(x => x.MemoryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workflow>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(2000);
            b.Property(x => x.EventType).HasMaxLength(100);
            b.Property(x => x.EventFilter).HasMaxLength(2000);
            b.Property(x => x.Cron).HasMaxLength(100);
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.Property(x => x.WebhookToken).HasMaxLength(64);
            b.ComplexProperty(x => x.Owner);
            JsonList(b.Property(x => x.Steps));

            // Webhook 请求只带令牌，跨空间按令牌查找。
            b.HasIndex(x => x.WebhookToken).IsUnique().HasFilter("webhook_token IS NOT NULL");

            // 事件分发按事件类型找订阅者；定时扫描只看启用的定时工作流。
            b.HasIndex(x => new { x.TenantId, x.EventType }).HasFilter("is_enabled AND trigger_kind = 'Event'");
            b.HasIndex(x => x.NextRunAt).HasFilter("is_enabled AND trigger_kind = 'Schedule'");
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowRun>(b =>
        {
            b.Property(x => x.WorkflowName).HasMaxLength(100);
            b.Property(x => x.EventType).HasMaxLength(100);
            b.Property(x => x.TriggerJson).HasColumnType("jsonb");
            b.Property(x => x.InputJson).HasColumnType("jsonb");
            b.ComplexProperty(x => x.Owner);
            b.ComplexProperty(x => x.TriggeredBy);
            JsonList(b.Property(x => x.Steps));

            b.HasIndex(x => new { x.WorkflowId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });

            // 引擎领取与重启恢复只关心未结束的运行。
            b.HasIndex(x => new { x.Status, x.CreatedAt }).HasFilter("status IN ('Queued', 'Running', 'Waiting')").HasDatabaseName("ix_workflow_runs_active");
            b.HasIndex(x => x.WaitingWorkItemId).HasFilter("waiting_work_item_id IS NOT NULL");
            b.HasIndex(x => x.WaitingApprovalId).HasFilter("waiting_approval_id IS NOT NULL");
            b.HasOne<Workflow>().WithMany().HasForeignKey(x => x.WorkflowId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowRunStep>(b =>
        {
            b.Property(x => x.StepKey).HasMaxLength(40);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Path).HasMaxLength(200);
            b.Property(x => x.InputJson).HasColumnType("jsonb");
            b.Property(x => x.OutputJson).HasColumnType("jsonb");
            b.HasIndex(x => new { x.RunId, x.Path }).IsUnique();
            b.HasOne<WorkflowRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmCompany>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Industry).HasMaxLength(100);
            b.Property(x => x.Website).HasMaxLength(300);
            b.Property(x => x.Phone).HasMaxLength(50);
            b.Property(x => x.Address).HasMaxLength(500);
            b.Property(x => x.Description).HasMaxLength(10_000);
            b.ComplexProperty(x => x.CreatedBy);

            // 名称包含匹配走三元组索引；标签过滤走数组 GIN 索引。
            b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.Tags).HasMethod("gin");
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
            JsonMap(b.Property(x => x.CustomFields));
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmContact>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Title).HasMaxLength(100);
            b.Property(x => x.Email).HasMaxLength(254);
            b.Property(x => x.Phone).HasMaxLength(50);
            b.Property(x => x.Description).HasMaxLength(10_000);
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.Email).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.Tags).HasMethod("gin");
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt });
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            JsonMap(b.Property(x => x.CustomFields));
            b.HasIndex(x => x.CompanyId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CrmCompany>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmStage>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(50);
            b.HasIndex(x => new { x.TenantId, x.Position });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CrmDeal>(b =>
        {
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Description).HasMaxLength(10_000);
            b.ComplexProperty(x => x.CreatedBy);
            b.HasIndex(x => x.Title).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => new { x.TenantId, x.Status, x.StageId });
            b.HasIndex(x => new { x.TenantId, x.ClosedAt }).HasFilter("status <> 'Open'");
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasIndex(x => x.StageId);
            b.HasIndex(x => x.CompanyId);
            b.HasIndex(x => x.ContactId);
            JsonMap(b.Property(x => x.CustomFields));
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CrmStage>().WithMany().HasForeignKey(x => x.StageId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CrmCompany>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<CrmContact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmActivity>(b =>
        {
            b.Property(x => x.Content).HasMaxLength(10_000);
            b.ComplexProperty(x => x.Author);

            // 三条时间线（客户、联系人、商机）各自按发生时间倒序翻页。
            b.HasIndex(x => new { x.CompanyId, x.OccurredAt });
            b.HasIndex(x => new { x.ContactId, x.OccurredAt });
            b.HasIndex(x => new { x.DealId, x.OccurredAt });
            b.HasIndex(x => new { x.TenantId, x.OccurredAt });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CrmCompany>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<CrmContact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<CrmDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CrmFieldDefinition>(b =>
        {
            b.Property(x => x.Key).HasMaxLength(40);
            b.Property(x => x.Label).HasMaxLength(50);
            b.Property(x => x.Description).HasMaxLength(500);
            JsonList(b.Property(x => x.Options));
            b.HasIndex(x => new { x.TenantId, x.RecordType, x.Key }).IsUnique();
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantSequence>(b =>
        {
            b.ToTable("tenant_sequences");
            b.HasKey(x => new { x.TenantId, x.Name });
            b.Property(x => x.Name).HasMaxLength(50);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErpProduct>(b =>
        {
            b.Property(x => x.Sku).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.Unit).HasMaxLength(20);
            b.Property(x => x.Category).HasMaxLength(50);
            b.Property(x => x.Description).HasMaxLength(10_000);
            b.Property(x => x.Price).HasPrecision(18, 4);
            b.Property(x => x.Cost).HasPrecision(18, 4);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.ReorderPoint).HasPrecision(18, 4);
            b.ComplexProperty(x => x.CreatedBy);

            // 编码在空间内唯一（保存前已转大写）；名称和编码的包含匹配走三元组索引。
            b.HasIndex(x => new { x.TenantId, x.Sku }).IsUnique();
            b.HasIndex(x => x.Sku).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
            b.HasIndex(x => new { x.TenantId, x.Category });
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErpWarehouse>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Address).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.Name });

            // 每个空间最多一个默认仓库。
            b.HasIndex(x => x.TenantId).IsUnique().HasFilter("is_default").HasDatabaseName("ix_erp_warehouses_default");
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErpStockLevel>(b =>
        {
            b.Property(x => x.OnHand).HasPrecision(18, 4);
            b.Property(x => x.Reserved).HasPrecision(18, 4);
            b.Property(x => x.AverageCost).HasPrecision(18, 6).HasDefaultValue(0m);

            // 出入库时 INSERT ... ON CONFLICT (product_id, warehouse_id) 依赖这个唯一索引。
            b.HasIndex(x => new { x.ProductId, x.WarehouseId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.WarehouseId });
            b.HasIndex(x => x.WarehouseId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ErpProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ErpWarehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ErpStockMovement>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 4);
            b.Property(x => x.BalanceAfter).HasPrecision(18, 4);
            b.Property(x => x.UnitCost).HasPrecision(18, 6);
            b.Property(x => x.CostAmount).HasPrecision(18, 2);
            b.Property(x => x.Note).HasMaxLength(500);
            b.ComplexProperty(x => x.Actor);
            b.HasIndex(x => new { x.ProductId, x.CreatedAt });
            b.HasIndex(x => new { x.WarehouseId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => x.SalesOrderId);
            b.HasIndex(x => x.PurchaseOrderId);
            b.HasIndex(x => x.DocumentId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ErpProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ErpWarehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ErpSalesOrder>().WithMany().HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<ErpPurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<ErpStockDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ErpStockDocument>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Note).HasMaxLength(500);
            b.ComplexProperty(x => x.CreatedBy);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("erp_stock_document_lines");
                l.WithOwner().HasForeignKey("DocumentId");
                l.Property<int>("Id").UseIdentityAlwaysColumn();
                l.HasKey("DocumentId", "Id");
                l.Property(x => x.Quantity).HasPrecision(18, 4);
                l.Property(x => x.UnitPrice).HasPrecision(18, 4);
                l.Property(x => x.Amount).HasPrecision(18, 2);
                l.Property(x => x.UnitCost).HasPrecision(18, 6);
                l.Property(x => x.CostAmount).HasPrecision(18, 2);
                l.Property(x => x.CostCurrency).HasMaxLength(3).IsFixedLength();
                l.HasIndex(x => x.ProductId);
                l.HasOne<ErpProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            });
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Kind, x.CreatedAt });
            b.HasIndex(x => x.SalesOrderId);
            b.HasIndex(x => x.PurchaseOrderId);
            b.HasIndex(x => x.WarehouseId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);

            // 出入库单是已发生的事实：有单据的订单只能取消、不能删除（删除只针对草稿和已取消的订单），这里用 Restrict 兜底。
            b.HasOne<ErpSalesOrder>().WithMany().HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ErpPurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ErpWarehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ErpSalesOrder>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Note).HasMaxLength(2_000);
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.ComplexProperty(x => x.CreatedBy);
            b.OwnsMany(x => x.Lines, OrderLines<ErpSalesOrder>("erp_sales_order_lines"));
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.ShippedAt }).HasFilter("status = 'Shipped'");
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasIndex(x => x.CompanyId);
            b.HasIndex(x => x.ContactId);
            b.HasIndex(x => x.DealId);
            b.HasIndex(x => x.WarehouseId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CrmCompany>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<CrmContact>().WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<CrmDeal>().WithMany().HasForeignKey(x => x.DealId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<ErpWarehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ErpPurchaseOrder>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(20);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Note).HasMaxLength(2_000);
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.ComplexProperty(x => x.CreatedBy);
            b.OwnsMany(x => x.Lines, OrderLines<ErpPurchaseOrder>("erp_purchase_order_lines"));
            b.HasIndex(x => new { x.TenantId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.ReceivedAt }).HasFilter("status = 'Received'");
            b.HasIndex(x => x.SupplierId);
            b.HasIndex(x => x.WarehouseId);
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CrmCompany>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<ErpWarehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        // 集群协调：在线实例与超长的广播消息，不属于任何空间。
        modelBuilder.Entity<ClusterInstance>(b =>
        {
            b.Property(x => x.HostName).HasMaxLength(256);
            b.HasIndex(x => x.LastSeenAt);
        });

        modelBuilder.Entity<ClusterMessage>(b => b.HasIndex(x => x.CreatedAt));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)))
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    /// <summary>
    /// 订单行单独成表、随订单读写（自增序号保持行的先后顺序）；产品被订单用过就不能删除。
    /// </summary>
    private static Action<Microsoft.EntityFrameworkCore.Metadata.Builders.OwnedNavigationBuilder<TOrder, ErpOrderLine>> OrderLines<TOrder>(string table)
        where TOrder : class
        => l =>
        {
            l.ToTable(table);
            l.WithOwner().HasForeignKey("OrderId");
            l.Property<int>("Id").UseIdentityAlwaysColumn();
            l.HasKey("OrderId", "Id");
            l.Property(x => x.Quantity).HasPrecision(18, 4);
            l.Property(x => x.UnitPrice).HasPrecision(18, 4);
            l.Property(x => x.Amount).HasPrecision(18, 2);
            l.Property(x => x.Delivered).HasPrecision(18, 4).HasDefaultValue(0m);
            l.Property(x => x.Returned).HasPrecision(18, 4).HasDefaultValue(0m);
            l.HasIndex(x => x.ProductId);
            l.HasOne<ErpProduct>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        };

    /// <summary>
    /// 列表存为 jsonb：按平台 JSON 约定序列化，按内容比较变化。
    /// </summary>
    private static void JsonList<T>(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<List<T>> property)
        => property
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, PlatformJson.Options),
                v => JsonSerializer.Deserialize<List<T>>(v, PlatformJson.Options) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, c) => JsonSerializer.Serialize(a, PlatformJson.Options) == JsonSerializer.Serialize(c, PlatformJson.Options),
                    v => JsonSerializer.Serialize(v, PlatformJson.Options).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, PlatformJson.Options), PlatformJson.Options)!));

    /// <summary>
    /// 键值对存为 jsonb（例如客户管理的自定义字段），默认值为空对象。
    /// </summary>
    private static void JsonMap(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<Dictionary<string, JsonElement>> property)
        => property
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, PlatformJson.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, PlatformJson.Options) ?? new Dictionary<string, JsonElement>(),
                new ValueComparer<Dictionary<string, JsonElement>>(
                    (a, c) => JsonSerializer.Serialize(a, PlatformJson.Options) == JsonSerializer.Serialize(c, PlatformJson.Options),
                    v => JsonSerializer.Serialize(v, PlatformJson.Options).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(v, PlatformJson.Options), PlatformJson.Options)!));

    private static readonly System.Reflection.MethodInfo ApplyTenantFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private void ApplyTenantFilter<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = e => e.TenantId == CurrentTenantId;
        var entity = modelBuilder.Entity<TEntity>();
        entity.HasQueryFilter(filter);

        // 已有以 tenant_id 打头的复合索引时不再单独建索引。
        var hasLeadingTenantIndex = entity.Metadata.GetIndexes()
            .Any(i => i.Properties[0].Name == nameof(ITenantScoped.TenantId));
        if (!hasLeadingTenantIndex)
        {
            entity.HasIndex(e => e.TenantId);
        }
    }
}
