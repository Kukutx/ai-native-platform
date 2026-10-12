using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Ai;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Agents;
using AiNative.Domain.Agents;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Agents;

[RequireFeature(Features.Agents.Manage)]
public sealed record CreateAgentCommand(SaveAgentRequest Request) : ICommand<AgentDto>;

[RequireFeature(Features.Agents.Manage)]
public sealed record UpdateAgentCommand(Guid Id, SaveAgentRequest Request) : ICommand<AgentDto>;

[RequireFeature(Features.Agents.Manage)]
public sealed record SetAgentPausedCommand(Guid Id, bool Paused) : ICommand<AgentDto>;

[RequireFeature(Features.Agents.Manage)]
public sealed record TerminateAgentCommand(Guid Id) : ICommand<AgentDto>;

[RequireFeature(Features.Agents.Wake)]
public sealed record WakeAgentCommand(Guid AgentId, WakeAgentRequest Request) : ICommand<WakeupDto>;

[RequireFeature(Features.Agents.View)]
public sealed record ListAgentsQuery : IQuery<IReadOnlyList<AgentDto>>;

[RequireFeature(Features.Agents.View)]
public sealed record GetAgentQuery(Guid Id) : IQuery<AgentDto>;

[RequireFeature(Features.Agents.View)]
public sealed record GetOrgChartQuery : IQuery<IReadOnlyList<OrgChartNodeDto>>;

public sealed class SaveAgentRequestValidator : AbstractValidator<SaveAgentRequest>
{
    public SaveAgentRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Instructions).MaximumLength(20_000);
        RuleFor(x => x.HeartbeatIntervalSeconds)
            .Must(v => v == 0 || v is >= 60 and <= 86_400)
            .WithMessage("心跳间隔为 0（不定时）或 60 到 86400 秒之间。");
        RuleFor(x => x.MonthlyBudgetUsd).GreaterThanOrEqualTo(0);
        RuleForEach(x => x.Permissions).NotEmpty().MaximumLength(100);
        RuleForEach(x => x.ApprovalRequiredActions).NotEmpty().MaximumLength(100);
    }
}

public sealed class CreateAgentCommandValidator : AbstractValidator<CreateAgentCommand>
{
    public CreateAgentCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveAgentRequestValidator());
}

public sealed class UpdateAgentCommandValidator : AbstractValidator<UpdateAgentCommand>
{
    public UpdateAgentCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveAgentRequestValidator());
}

public sealed class WakeAgentCommandValidator : AbstractValidator<WakeAgentCommand>
{
    public WakeAgentCommandValidator() => RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(2_000);
}

internal sealed class AgentCommandHandlers(
    IAppDbContext db,
    IModelGateway models,
    IWakeupQueue wakeups,
    ITenantContext tenant,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<CreateAgentCommand, AgentDto>,
    IRequestHandler<UpdateAgentCommand, AgentDto>,
    IRequestHandler<SetAgentPausedCommand, AgentDto>,
    IRequestHandler<TerminateAgentCommand, AgentDto>,
    IRequestHandler<WakeAgentCommand, WakeupDto>
{
    public async Task<AgentDto> HandleAsync(CreateAgentCommand request, CancellationToken cancellationToken)
    {
        var selection = ResolveModel(request.Request);
        var agent = new Agent
        {
            Name = request.Request.Name.Trim(),
            Title = request.Request.Title.Trim(),
            ModelProvider = selection.Provider,
            Model = selection.Model,
        };
        await ApplyAsync(agent, request.Request, cancellationToken).ConfigureAwait(false);
        db.Agents.Add(agent);
        changes.Publish(RealtimeEntities.Agent, agent.Id, RealtimeChangeKind.Created);
        return agent.ToDto(0);
    }

    public async Task<AgentDto> HandleAsync(UpdateAgentCommand request, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        var selection = ResolveModel(request.Request);
        agent.Name = request.Request.Name.Trim();
        agent.Title = request.Request.Title.Trim();
        agent.ModelProvider = selection.Provider;
        agent.Model = selection.Model;
        await ApplyAsync(agent, request.Request, cancellationToken).ConfigureAwait(false);
        changes.Publish(RealtimeEntities.Agent, agent.Id, RealtimeChangeKind.Updated);
        return agent.ToDto(await AgentSpend.ForAgentAsync(db, agent.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<AgentDto> HandleAsync(SetAgentPausedCommand request, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (request.Paused)
        {
            agent.Pause("手动暂停");
        }
        else
        {
            agent.Resume();
        }

        changes.Publish(RealtimeEntities.Agent, agent.Id, RealtimeChangeKind.Updated);
        return agent.ToDto(await AgentSpend.ForAgentAsync(db, agent.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<AgentDto> HandleAsync(TerminateAgentCommand request, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        agent.Status = AgentStatus.Terminated;
        agent.PauseReason = "已终止";
        var now = clock.GetUtcNow();
        await db.WakeupRequests
            .Where(w => w.AgentId == agent.Id && w.Status == WakeupStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.Status, WakeupStatus.Cancelled).SetProperty(w => w.FinishedAt, now), cancellationToken)
            .ConfigureAwait(false);

        // 下属改为向被终止者的上级汇报，组织不断链。
        var reports = await db.Agents.Where(a => a.ReportsToId == agent.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var report in reports)
        {
            report.ReportsToId = agent.ReportsToId;
        }

        changes.Publish(RealtimeEntities.Agent, agent.Id, RealtimeChangeKind.Updated);
        return agent.ToDto(await AgentSpend.ForAgentAsync(db, agent.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<WakeupDto> HandleAsync(WakeAgentCommand request, CancellationToken cancellationToken)
    {
        var agent = await FindAsync(request.AgentId, cancellationToken).ConfigureAwait(false);
        if (agent.Status == AgentStatus.Terminated)
        {
            throw new ConflictException("agent.terminated", "已终止的智能体不能唤醒。");
        }

        if (request.Request.WorkItemId is { } workItemId
            && !await db.WorkItems.AnyAsync(w => w.Id == workItemId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException("work_item", workItemId);
        }

        var item = new WakeupItem
        {
            Source = tenant.Actor.IsAgent ? WakeupSource.Delegation : WakeupSource.OnDemand,
            Reason = string.IsNullOrWhiteSpace(request.Request.Reason) ? "手动唤醒" : request.Request.Reason.Trim(),
            WorkItemId = request.Request.WorkItemId,
            RequestedAt = clock.GetUtcNow(),
        };
        var id = await wakeups.EnqueueAsync(agent.Id, item, tenant.Actor, cancellationToken: cancellationToken).ConfigureAwait(false);
        var wakeup = await db.WakeupRequests.AsNoTracking().FirstAsync(w => w.Id == id, cancellationToken).ConfigureAwait(false);
        return wakeup.ToDto();
    }

    private ModelSelection ResolveModel(SaveAgentRequest request)
    {
        var provider = string.IsNullOrWhiteSpace(request.ModelProvider) ? models.Default.Provider : request.ModelProvider.Trim();
        if (!models.HasProvider(provider))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                [nameof(SaveAgentRequest.ModelProvider)] = [$"未配置模型提供方 {provider}。"],
            });
        }

        var model = string.IsNullOrWhiteSpace(request.Model)
            ? (provider == models.Default.Provider ? models.Default.Model : models.Describe().Providers.First(p => p.Name == provider).Models.FirstOrDefault() ?? models.Default.Model)
            : request.Model.Trim();
        return new ModelSelection(provider, model);
    }

    private async Task ApplyAsync(Agent agent, SaveAgentRequest request, CancellationToken cancellationToken)
    {
        agent.Instructions = request.Instructions.Trim();
        // 不能给智能体超出自己的权限，否则可以建一个全权限的智能体替自己办事；没指定时套用默认权限里自己能授予的部分。
        var existing = agent.Permissions;
        List<string> permissions = request.Permissions is { } explicitPermissions
            ? [.. explicitPermissions.Select(p => p.Trim()).Distinct()]
            : [.. AgentDefaults.Permissions.Where(p => existing.Contains(p) || tenant.Can(p))];
        GrantCeiling.Ensure(tenant, permissions, existing);
        agent.Permissions = permissions;
        agent.ApprovalRequiredActions = [.. request.ApprovalRequiredActions.Select(a => a.Trim()).Distinct()];
        agent.HeartbeatIntervalSeconds = request.HeartbeatIntervalSeconds;
        agent.MonthlyBudgetUsd = request.MonthlyBudgetUsd;

        Agent? manager = null;
        if (request.ReportsToId is { } managerId)
        {
            manager = await db.Agents.FirstOrDefaultAsync(a => a.Id == managerId, cancellationToken).ConfigureAwait(false)
                      ?? throw new NotFoundException("agent", managerId);
        }

        var lines = await db.Agents.AsNoTracking()
            .Select(a => new { a.Id, a.ReportsToId })
            .ToDictionaryAsync(a => a.Id, a => a.ReportsToId, cancellationToken)
            .ConfigureAwait(false);
        agent.AssignManager(manager, lines);
    }

    private async Task<Agent> FindAsync(Guid id, CancellationToken cancellationToken)
        => await db.Agents.FirstOrDefaultAsync(a => a.Id == id, cancellationToken).ConfigureAwait(false)
           ?? throw new NotFoundException("agent", id);
}

internal sealed class AgentQueryHandlers(IAppDbContext db, TimeProvider clock) :
    IRequestHandler<ListAgentsQuery, IReadOnlyList<AgentDto>>,
    IRequestHandler<GetAgentQuery, AgentDto>,
    IRequestHandler<GetOrgChartQuery, IReadOnlyList<OrgChartNodeDto>>
{
    public async Task<IReadOnlyList<AgentDto>> HandleAsync(ListAgentsQuery request, CancellationToken cancellationToken)
    {
        var agents = await db.Agents.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var spend = await AgentSpend.ForMonthAsync(db, clock.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
        return [.. agents.Select(a => a.ToDto(spend.GetValueOrDefault(a.Id)))];
    }

    public async Task<AgentDto> HandleAsync(GetAgentQuery request, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.Id, cancellationToken).ConfigureAwait(false)
                    ?? throw new NotFoundException("agent", request.Id);
        return agent.ToDto(await AgentSpend.ForAgentAsync(db, agent.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<OrgChartNodeDto>> HandleAsync(GetOrgChartQuery request, CancellationToken cancellationToken)
    {
        var agents = await HandleAsync(new ListAgentsQuery(), cancellationToken).ConfigureAwait(false);
        var active = agents.Where(a => a.Status != AgentStatus.Terminated).ToList();
        var ids = active.Select(a => a.Id).ToHashSet();
        var byManager = active.ToLookup(a => a.ReportsToId is { } m && ids.Contains(m) ? m : (Guid?)null);

        OrgChartNodeDto Build(AgentDto agent, int depth) => new(
            agent,
            depth > 32 ? [] : [.. byManager[agent.Id].Select(r => Build(r, depth + 1))]);

        return [.. byManager[null].Select(a => Build(a, 0))];
    }
}
