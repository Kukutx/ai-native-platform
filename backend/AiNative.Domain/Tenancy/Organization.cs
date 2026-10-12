using AiNative.Domain.Common;

namespace AiNative.Domain.Tenancy;

/// <summary>
/// 租户内的组织单元，支持任意层级。Path 为物化路径（/根id/子id/），便于按子树查询。
/// </summary>
public sealed class Organization : TenantEntity
{
    public required string Name { get; set; }

    public Guid? ParentId { get; set; }

    public string Path { get; set; } = "/";

    public int Depth { get; set; }

    public void PlaceUnder(Organization? parent)
    {
        if (parent is null)
        {
            ParentId = null;
            Path = $"/{Id}/";
            Depth = 0;
            return;
        }

        if (parent.Path.Contains($"/{Id}/", StringComparison.Ordinal))
        {
            throw new DomainException("organization.cycle", "组织不能挂到自己的下级之下。");
        }

        ParentId = parent.Id;
        Path = $"{parent.Path}{Id}/";
        Depth = parent.Depth + 1;
    }
}
