using System.Linq.Expressions;
using System.Text.Json;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Chat;
using AiNative.Domain.Common;
using AiNative.Domain.Governance;
using AiNative.Domain.Identity;
using AiNative.Domain.Knowledge;
using AiNative.Domain.Modules;
using AiNative.Domain.Tenancy;
using AiNative.Domain.Work;
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

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<CostEvent> CostEvents => Set<CostEvent>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

    public DbSet<KnowledgeDocument> KnowledgeDocuments => Set<KnowledgeDocument>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<MemoryEntry> MemoryEntries => Set<MemoryEntry>();

    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

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
        // 向量检索（knowledge_chunks.embedding）依赖 pgvector 扩展。
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Tenant>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Slug).HasMaxLength(64);
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
            b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<WorkItem>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.SetNull);
            b.HasOne<Agent>().WithMany().HasForeignKey(x => x.AssigneeAgentId).OnDelete(DeleteBehavior.SetNull);
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

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantScoped).IsAssignableFrom(t.ClrType)))
        {
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [modelBuilder]);
        }
    }

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
