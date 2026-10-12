namespace AiNative.Domain.Common;

public enum ActorKind
{
    System = 0,
    User = 1,
    Agent = 2,
}

/// <summary>
/// 操作发起者。人和智能体在平台里是同等的执行主体，所有写操作都记录是谁做的。
/// </summary>
public readonly record struct Actor(ActorKind Kind, Guid? Id)
{
    public static Actor System => new(ActorKind.System, null);

    public static Actor ForUser(Guid userId) => new(ActorKind.User, userId);

    public static Actor ForAgent(Guid agentId) => new(ActorKind.Agent, agentId);

    public bool IsAgent => Kind == ActorKind.Agent;

    public bool IsUser => Kind == ActorKind.User;

    public override string ToString() => Id is { } id ? $"{Kind.ToString().ToLowerInvariant()}:{id}" : "system";
}
