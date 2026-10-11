using AiNative.Domain.Common;

namespace AiNative.Domain.Crm;

/// <summary>
/// 客户（公司或组织）。联系人、商机和跟进记录都可以挂在它下面。
/// </summary>
public sealed class CrmCompany : TenantEntity
{
    public required string Name { get; set; }

    public string Industry { get; set; } = string.Empty;

    public string Website { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// 负责这个客户的成员。
    /// </summary>
    public Guid? OwnerUserId { get; set; }

    public Actor CreatedBy { get; set; }

    /// <summary>
    /// 最近一次跟进的时间（自己的、名下联系人与商机的都算），用于找出久未联系的客户。
    /// </summary>
    public DateTimeOffset? LastActivityAt { get; set; }
}

/// <summary>
/// 联系人。可以不属于任何客户（例如个人客户或还没归档的线索）。
/// </summary>
public sealed class CrmContact : TenantEntity
{
    public required string Name { get; set; }

    public Guid? CompanyId { get; set; }

    /// <summary>
    /// 职位。
    /// </summary>
    public string Title { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public string Phone { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = [];

    public Guid? OwnerUserId { get; set; }

    public Actor CreatedBy { get; set; }

    public DateTimeOffset? LastActivityAt { get; set; }
}
