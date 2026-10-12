using AiNative.Domain.Common;

namespace AiNative.Contracts.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// 操作者的展示信息。Name 由服务端解析（用户显示名或智能体名称）。
/// </summary>
public sealed record ActorDto(ActorKind Kind, Guid? Id, string? Name);
