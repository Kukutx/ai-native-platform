using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Crm;
using AiNative.Domain.Crm;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Crm;

[RequireFeature(CrmFeatures.View)]
public sealed record ListCrmStagesQuery : IQuery<IReadOnlyList<CrmStageDto>>;

[RequireFeature(CrmFeatures.Configure)]
public sealed record SaveCrmStagesCommand(SaveCrmStagesRequest Request) : ICommand<IReadOnlyList<CrmStageDto>>;

public static class CrmDefaultStages
{
    public const int MaxStages = 20;

    public static IReadOnlyList<(string Name, int Probability, CrmStageKind Kind)> All { get; } =
    [
        ("线索", 10, CrmStageKind.Open),
        ("需求确认", 25, CrmStageKind.Open),
        ("方案报价", 50, CrmStageKind.Open),
        ("商务谈判", 75, CrmStageKind.Open),
        ("赢单", 100, CrmStageKind.Won),
        ("输单", 0, CrmStageKind.Lost),
    ];
}

public sealed class SaveCrmStagesCommandValidator : AbstractValidator<SaveCrmStagesCommand>
{
    public SaveCrmStagesCommandValidator()
    {
        RuleFor(x => x.Request.Stages).NotEmpty().WithMessage("至少需要一个销售阶段。")
            .Must(s => s.Count <= CrmDefaultStages.MaxStages).WithMessage($"销售阶段最多 {CrmDefaultStages.MaxStages} 个。")
            .Must(s => s.Any(x => x.Kind == CrmStageKind.Open)).WithMessage("至少需要一个进行中的阶段，新商机会放进第一个进行中的阶段。")
            .Must(s => s.Select(x => x.Name.Trim().ToUpperInvariant()).Distinct().Count() == s.Count).WithMessage("阶段名称不能重复。")
            .Must(s => s.Where(x => x.Id.HasValue).Select(x => x.Id).Distinct().Count() == s.Count(x => x.Id.HasValue)).WithMessage("同一个阶段不能出现两次。")
            .OverridePropertyName("stages");
        RuleForEach(x => x.Request.Stages).ChildRules(stage =>
        {
            stage.RuleFor(s => s.Name).NotEmpty().WithMessage("请填写阶段名称。").MaximumLength(50).WithMessage("阶段名称最多 50 个字。");
            stage.RuleFor(s => s.Probability).InclusiveBetween(0, 100).WithMessage("成交概率在 0 到 100 之间。");
        }).OverridePropertyName("stages");
    }
}

internal sealed class CrmStageHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    ITransactionLock locks,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListCrmStagesQuery, IReadOnlyList<CrmStageDto>>,
    IRequestHandler<SaveCrmStagesCommand, IReadOnlyList<CrmStageDto>>
{
    public async Task<IReadOnlyList<CrmStageDto>> HandleAsync(ListCrmStagesQuery request, CancellationToken cancellationToken)
        => [.. (await db.CrmStages.AsNoTracking().OrderBy(s => s.Position).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(s => s.ToDto())];

    /// <summary>
    /// 按提交的顺序整体替换阶段列表。去掉的阶段里不能还有商机；阶段类型改变时，其中商机的状态随之改变。
    /// </summary>
    public async Task<IReadOnlyList<CrmStageDto>> HandleAsync(SaveCrmStagesCommand request, CancellationToken cancellationToken)
    {
        // 两个人同时保存时串行执行，后保存的基于先保存的结果判断。
        await locks.AcquireAsync($"crm-stages:{tenant.TenantId}", cancellationToken).ConfigureAwait(false);
        var existing = await db.CrmStages.ToDictionaryAsync(s => s.Id, cancellationToken).ConfigureAwait(false);
        foreach (var item in request.Request.Stages)
        {
            if (item.Id is { } id && !existing.ContainsKey(id))
            {
                throw new NotFoundException("crm_stage", id);
            }
        }

        var kept = request.Request.Stages.Where(s => s.Id.HasValue).Select(s => s.Id!.Value).ToHashSet();
        var removed = existing.Values.Where(s => !kept.Contains(s.Id)).ToList();
        if (removed.Count > 0)
        {
            var removedIds = removed.Select(s => s.Id).ToList();
            var inUse = await db.CrmDeals.Where(d => removedIds.Contains(d.StageId))
                .GroupBy(d => d.StageId).Select(g => new { g.Key, Count = g.Count() })
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (inUse is not null)
            {
                throw new ConflictException("crm_stage.in_use", $"阶段「{existing[inUse.Key].Name}」里还有 {inUse.Count} 个商机，请先把它们移到其他阶段再删除。");
            }

            db.CrmStages.RemoveRange(removed);
        }

        var now = clock.GetUtcNow();
        var result = new List<CrmStage>();
        for (var position = 0; position < request.Request.Stages.Count; position++)
        {
            var item = request.Request.Stages[position];
            CrmStage stage;
            if (item.Id is { } id)
            {
                stage = existing[id];
                if (stage.Kind != item.Kind)
                {
                    await SyncDealStatusAsync(stage.Id, item.Kind, now, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                stage = new CrmStage { Name = item.Name.Trim() };
                db.CrmStages.Add(stage);
            }

            stage.Name = item.Name.Trim();
            stage.Position = position;
            stage.Probability = item.Probability;
            stage.Kind = item.Kind;
            result.Add(stage);
        }

        changes.Publish(CrmRealtime.Stage, tenant.TenantId, RealtimeChangeKind.Updated);
        return [.. result.Select(s => s.ToDto())];
    }

    /// <summary>
    /// 阶段类型变化后同步其中商机的状态：变成赢单或输单时记下结束时间（已结束的保留原时间），变回进行中则清除。
    /// </summary>
    private async Task SyncDealStatusAsync(Guid stageId, CrmStageKind kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deals = db.CrmDeals.Where(d => d.StageId == stageId);
        var affected = kind switch
        {
            CrmStageKind.Open => await deals.ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, CrmDealStatus.Open).SetProperty(d => d.ClosedAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false),
            _ => await deals.ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, kind == CrmStageKind.Won ? CrmDealStatus.Won : CrmDealStatus.Lost)
                    .SetProperty(d => d.ClosedAt, d => d.ClosedAt ?? now), cancellationToken).ConfigureAwait(false),
        };
        if (affected > 0)
        {
            changes.Publish(CrmRealtime.Deal, stageId, RealtimeChangeKind.Updated);
        }
    }
}

/// <summary>
/// 新空间自带一套常用销售阶段，开箱即可录入商机。
/// </summary>
internal sealed class CrmTenantInitializer(IAppDbContext db) : ITenantInitializer
{
    public Task InitializeAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        db.CrmStages.AddRange(CrmDefaultStages.All.Select((s, i) => new CrmStage
        {
            TenantId = tenantId,
            Name = s.Name,
            Position = i,
            Probability = s.Probability,
            Kind = s.Kind,
        }));
        return Task.CompletedTask;
    }
}
