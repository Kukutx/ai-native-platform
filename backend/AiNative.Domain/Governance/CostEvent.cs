using AiNative.Domain.Common;

namespace AiNative.Domain.Governance;

/// <summary>
/// 一次模型调用的用量与费用。预算、成本看板都从这里聚合。
/// </summary>
public sealed class CostEvent : TenantEntity
{
    public Actor Actor { get; set; }

    public Guid? RunId { get; set; }

    public Guid? ConversationId { get; set; }

    public required string Provider { get; set; }

    public required string Model { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }

    public decimal CostUsd { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}
