using AiNative.Domain.Common;

namespace AiNative.Domain.Knowledge;

/// <summary>
/// 记忆归属：整个空间共享，或只属于某个智能体 / 某个成员。
/// </summary>
public enum MemoryScope
{
    Tenant = 0,
    Agent = 1,
    User = 2,
}

/// <summary>
/// 长期记忆：一条值得跨会话、跨心跳记住的事实、偏好或约定。
/// 智能体每轮都从数据库现取上下文，记忆让它记得"上次学到的东西"。
/// </summary>
public sealed class MemoryEntry : TenantEntity
{
    public MemoryScope Scope { get; set; }

    /// <summary>
    /// 归属的智能体或成员；共享记忆为空。
    /// </summary>
    public Guid? OwnerId { get; set; }

    public required string Content { get; set; }

    /// <summary>
    /// 规范化正文的 SHA-256，同一归属下相同内容只保留一条。
    /// </summary>
    public required string ContentHash { get; set; }

    public List<string> Tags { get; set; } = [];

    public Actor CreatedBy { get; set; }

    /// <summary>
    /// 记下这条记忆时所在的执行与任务，便于追溯来源。
    /// </summary>
    public Guid? RunId { get; set; }

    public Guid? WorkItemId { get; set; }

    public int RecallCount { get; set; }

    public DateTimeOffset? LastRecalledAt { get; set; }

    public static (MemoryScope Scope, Guid? OwnerId) OwnedBy(Actor actor, bool shared)
    {
        if (shared)
        {
            return (MemoryScope.Tenant, null);
        }

        return actor.Kind switch
        {
            ActorKind.Agent => (MemoryScope.Agent, actor.Id),
            ActorKind.User => (MemoryScope.User, actor.Id),
            _ => (MemoryScope.Tenant, null),
        };
    }

    /// <summary>
    /// 该操作者能否看到这条记忆：共享的人人可见，私有的只有本人可见。
    /// </summary>
    public bool IsVisibleTo(Actor actor) => Scope switch
    {
        MemoryScope.Tenant => true,
        MemoryScope.Agent => actor.IsAgent && actor.Id == OwnerId,
        MemoryScope.User => actor.IsUser && actor.Id == OwnerId,
        _ => false,
    };
}
