using AiNative.Domain.Common;

namespace AiNative.Domain.Identity;

public enum MembershipStatus
{
    Active = 0,
    Suspended = 1,
}

/// <summary>
/// 用户在某个租户中的身份。角色由租户自行定义（Role），这里只记引用。
/// </summary>
public sealed class Membership : TenantEntity
{
    public Guid UserId { get; set; }

    /// <summary>
    /// 归属的组织单元；为空表示租户级成员。
    /// </summary>
    public Guid? OrganizationId { get; set; }

    public bool IsOwner { get; set; }

    public MembershipStatus Status { get; set; } = MembershipStatus.Active;

    public List<Guid> RoleIds { get; set; } = [];
}
