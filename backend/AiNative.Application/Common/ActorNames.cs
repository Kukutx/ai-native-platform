using AiNative.Application.Abstractions;
using AiNative.Contracts.Common;
using AiNative.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Common;

/// <summary>
/// 批量把 Actor 解析成带名称的 ActorDto，避免列表接口逐条查询。
/// </summary>
public static class ActorNames
{
    public static async Task<Func<Actor, ActorDto>> ResolveAsync(
        IAppDbContext db,
        IEnumerable<Actor> actors,
        CancellationToken cancellationToken)
    {
        var list = actors.ToList();
        var userIds = list.Where(a => a.Kind == ActorKind.User && a.Id.HasValue).Select(a => a.Id!.Value).Distinct().ToList();
        var agentIds = list.Where(a => a.Kind == ActorKind.Agent && a.Id.HasValue).Select(a => a.Id!.Value).Distinct().ToList();

        var users = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken).ConfigureAwait(false);
        var agents = agentIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Agents.AsNoTracking().Where(a => agentIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken).ConfigureAwait(false);

        return actor => actor.Kind switch
        {
            ActorKind.User when actor.Id is { } id => new ActorDto(actor.Kind, id, users.GetValueOrDefault(id)),
            ActorKind.Agent when actor.Id is { } id => new ActorDto(actor.Kind, id, agents.GetValueOrDefault(id)),
            _ => new ActorDto(ActorKind.System, null, "系统"),
        };
    }
}
