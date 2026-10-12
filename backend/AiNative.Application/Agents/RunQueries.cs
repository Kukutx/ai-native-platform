using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Contracts.Agents;
using AiNative.Contracts.Common;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Agents;

[RequireFeature(Features.Runs.View)]
public sealed record ListRunsQuery(Guid? AgentId = null, int Page = 1, int PageSize = 30) : IQuery<PagedResult<RunDto>>;

[RequireFeature(Features.Runs.View)]
public sealed record GetRunQuery(Guid Id) : IQuery<RunDetailDto>;

internal sealed class RunQueryHandlers(IAppDbContext db) :
    IRequestHandler<ListRunsQuery, PagedResult<RunDto>>,
    IRequestHandler<GetRunQuery, RunDetailDto>
{
    public async Task<PagedResult<RunDto>> HandleAsync(ListRunsQuery request, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(request.Page, request.PageSize, 100);
        var query = db.HeartbeatRuns.AsNoTracking();
        if (request.AgentId is { } agentId)
        {
            query = query.Where(r => r.AgentId == agentId);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var runs = await query.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await AgentNamesAsync(runs.Select(r => r.AgentId), cancellationToken).ConfigureAwait(false);
        return new PagedResult<RunDto>([.. runs.Select(r => r.ToDto(names.GetValueOrDefault(r.AgentId, "?")))], total, page, pageSize);
    }

    public async Task<RunDetailDto> HandleAsync(GetRunQuery request, CancellationToken cancellationToken)
    {
        var run = await db.HeartbeatRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken).ConfigureAwait(false)
                  ?? throw new NotFoundException("run", request.Id);
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == run.Id).OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var wakeup = run.WakeupRequestId is { } wakeupId
            ? await db.WakeupRequests.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wakeupId, cancellationToken).ConfigureAwait(false)
            : null;
        var names = await AgentNamesAsync([run.AgentId], cancellationToken).ConfigureAwait(false);
        return new RunDetailDto(
            run.ToDto(names.GetValueOrDefault(run.AgentId, "?")),
            [.. events.Select(e => new RunEventDto(e.RunId, run.AgentId, e.Seq, e.Type, PlatformJson.Parse(e.DataJson), e.CreatedAt))],
            wakeup?.ToDto());
    }

    private async Task<Dictionary<Guid, string>> AgentNamesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.Distinct().ToList();
        return await db.Agents.AsNoTracking().Where(a => set.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken).ConfigureAwait(false);
    }
}
