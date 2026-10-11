namespace AiNative.Domain.Tenancy;

/// <summary>
/// 租户内的编号序列（例如销售订单 SO、采购订单 PO），每个名称一行，按需创建，原子自增。
/// </summary>
public sealed class TenantSequence
{
    public Guid TenantId { get; set; }

    public required string Name { get; set; }

    public long Value { get; set; }
}
