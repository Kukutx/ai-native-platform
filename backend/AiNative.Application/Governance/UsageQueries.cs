using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Agents;
using AiNative.Contracts.Platform;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Governance;

[RequireFeature(Features.Platform.UsageView)]
public sealed record GetUsageSummaryQuery : IQuery<UsageSummaryDto>;

internal sealed class UsageQueryHandler(IAppDbContext db, TimeProvider clock) : IRequestHandler<GetUsageSummaryQuery, UsageSummaryDto>
{
    public async Task<UsageSummaryDto> HandleAsync(GetUsageSummaryQuery request, CancellationToken cancellationToken)
    {
        var start = BillingPeriod.MonthStart(clock.GetUtcNow());
        var month = db.CostEvents.AsNoTracking().Where(c => c.OccurredAt >= start);
        var total = await month.SumAsync(c => c.CostUsd, cancellationToken).ConfigureAwait(false);
        var byModel = await month
            .GroupBy(c => new { c.Provider, c.Model })
            .Select(g => new ModelUsageDto(g.Key.Provider, g.Key.Model, g.Sum(c => c.InputTokens), g.Sum(c => c.OutputTokens), g.Sum(c => c.CostUsd)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var spend = await AgentSpend.ForMonthAsync(db, clock.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
        var spentIds = spend.Keys.ToList();
        var agents = await db.Agents.AsNoTracking().Where(a => a.Status != Domain.Agents.AgentStatus.Terminated || spentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.MonthlyBudgetUsd })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byAgent = agents
            .Select(a => new AgentUsageDto(a.Id, a.Name, spend.GetValueOrDefault(a.Id), a.MonthlyBudgetUsd))
            .OrderByDescending(a => a.CostUsd)
            .ToList();
        return new UsageSummaryDto(start, total, byAgent, [.. byModel.OrderByDescending(m => m.CostUsd)]);
    }
}
