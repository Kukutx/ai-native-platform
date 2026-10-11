namespace AiNative.Domain.Common;

/// <summary>
/// 所有持久化实体的基类：主键使用时间有序的 UUIDv7，索引局部性好且可在应用侧生成。
/// 创建/更新时间由持久化层拦截器统一写入。
/// </summary>
public abstract class Entity
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// 租户隔离的数据。持久化层对实现此接口的实体自动加租户过滤，并在新增时写入当前租户。
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; set; }
}

public abstract class TenantEntity : Entity, ITenantScoped
{
    public Guid TenantId { get; set; }
}
