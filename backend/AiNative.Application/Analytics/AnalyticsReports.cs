using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Common;
using AiNative.Application.Realtime;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Analytics;
using AiNative.Domain.Analytics;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Analytics;

[RequireFeature(AnalyticsFeatures.View)]
public sealed record ListAnalyticsReportsQuery : IQuery<IReadOnlyList<AnalyticsReportDto>>;

[RequireFeature(AnalyticsFeatures.View)]
public sealed record GetAnalyticsReportQuery(Guid Id) : IQuery<AnalyticsReportDto>;

[RequireFeature(AnalyticsFeatures.Manage)]
public sealed record CreateAnalyticsReportCommand(SaveAnalyticsReportRequest Request) : ICommand<AnalyticsReportDto>;

[RequireFeature(AnalyticsFeatures.Manage)]
public sealed record UpdateAnalyticsReportCommand(Guid Id, SaveAnalyticsReportRequest Request) : ICommand<AnalyticsReportDto>;

[RequireFeature(AnalyticsFeatures.Manage)]
public sealed record DeleteAnalyticsReportCommand(Guid Id) : ICommand<Unit>;

[RequireFeature(AnalyticsFeatures.Manage)]
public sealed record ReorderAnalyticsReportsCommand(IReadOnlyList<Guid> Ids) : ICommand<IReadOnlyList<AnalyticsReportDto>>;

public sealed class SaveAnalyticsReportRequestValidator : AbstractValidator<SaveAnalyticsReportRequest>
{
    public SaveAnalyticsReportRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("请填写报表名称。").Text(100, "名称");
        RuleFor(x => x.Description).Text(1_000, "说明");
        RuleFor(x => x.Metric).NotEmpty().WithMessage("请选择指标。").MaximumLength(64);
        RuleFor(x => x.Breakdown).MaximumLength(32);
        RuleFor(x => x.Currency!).Matches("^[A-Za-z]{3}$").When(x => !string.IsNullOrWhiteSpace(x.Currency))
            .WithMessage("货币用三位字母代码，例如 CNY。");
        RuleFor(x => x.Range).IsInEnum();
        RuleFor(x => x.Interval).IsInEnum();
        RuleFor(x => x.Chart).IsInEnum();
    }
}

public sealed class CreateAnalyticsReportCommandValidator : AbstractValidator<CreateAnalyticsReportCommand>
{
    public CreateAnalyticsReportCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveAnalyticsReportRequestValidator());
}

public sealed class UpdateAnalyticsReportCommandValidator : AbstractValidator<UpdateAnalyticsReportCommand>
{
    public UpdateAnalyticsReportCommandValidator() => RuleFor(x => x.Request).SetValidator(new SaveAnalyticsReportRequestValidator());
}

public sealed class ReorderAnalyticsReportsCommandValidator : AbstractValidator<ReorderAnalyticsReportsCommand>
{
    public ReorderAnalyticsReportsCommandValidator()
    {
        RuleFor(x => x.Ids).NotEmpty().Must(ids => ids.Count <= 200).WithMessage("一次最多排序 200 个报表。")
            .Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("报表不能重复。");
    }
}

internal sealed class AnalyticsReportHandlers(
    IAppDbContext db,
    ITenantContext tenant,
    MetricCatalog catalog,
    IChangeFeed changes,
    TimeProvider clock) :
    IRequestHandler<ListAnalyticsReportsQuery, IReadOnlyList<AnalyticsReportDto>>,
    IRequestHandler<GetAnalyticsReportQuery, AnalyticsReportDto>,
    IRequestHandler<CreateAnalyticsReportCommand, AnalyticsReportDto>,
    IRequestHandler<UpdateAnalyticsReportCommand, AnalyticsReportDto>,
    IRequestHandler<DeleteAnalyticsReportCommand, Unit>,
    IRequestHandler<ReorderAnalyticsReportsCommand, IReadOnlyList<AnalyticsReportDto>>
{
    public async Task<IReadOnlyList<AnalyticsReportDto>> HandleAsync(ListAnalyticsReportsQuery request, CancellationToken cancellationToken)
    {
        var reports = await db.AnalyticsReports.AsNoTracking().OrderBy(r => r.Position).ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return await ToDtosAsync(reports, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AnalyticsReportDto> HandleAsync(GetAnalyticsReportQuery request, CancellationToken cancellationToken)
    {
        var report = await db.AnalyticsReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("analytics_report", request.Id);
        return (await ToDtosAsync([report], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<AnalyticsReportDto> HandleAsync(CreateAnalyticsReportCommand request, CancellationToken cancellationToken)
    {
        await EnsureMetricAsync(request.Request, cancellationToken).ConfigureAwait(false);
        var last = await db.AnalyticsReports.MaxAsync(r => (int?)r.Position, cancellationToken).ConfigureAwait(false);
        var report = new AnalyticsReport
        {
            Name = request.Request.Name.Trim(),
            Metric = request.Request.Metric.Trim(),
            Position = (last ?? -1) + 1,
            CreatedBy = tenant.Actor,
        };
        Apply(report, request.Request);
        db.AnalyticsReports.Add(report);
        changes.Publish(AnalyticsRealtime.Report, report.Id, RealtimeChangeKind.Created);
        return (await ToDtosAsync([report], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<AnalyticsReportDto> HandleAsync(UpdateAnalyticsReportCommand request, CancellationToken cancellationToken)
    {
        var report = await db.AnalyticsReports.FirstOrDefaultAsync(r => r.Id == request.Id, cancellationToken).ConfigureAwait(false)
                     ?? throw new NotFoundException("analytics_report", request.Id);
        var now = clock.GetUtcNow();
        if (request.Request.ExpectedUpdatedAt is { } expected)
        {
            await OptimisticConcurrency.EnsureUnchangedAsync(
                db.AnalyticsReports, report.Id, expected, now, "analytics_report.modified", "报表刚被其他人修改过，已刷新为最新内容，请确认后再保存。", cancellationToken)
                .ConfigureAwait(false);
        }

        await EnsureMetricAsync(request.Request, cancellationToken).ConfigureAwait(false);
        report.Name = request.Request.Name.Trim();
        report.Metric = request.Request.Metric.Trim();
        Apply(report, request.Request);
        report.UpdatedAt = now;
        changes.Publish(AnalyticsRealtime.Report, report.Id, RealtimeChangeKind.Updated);
        return (await ToDtosAsync([report], cancellationToken).ConfigureAwait(false))[0];
    }

    public async Task<Unit> HandleAsync(DeleteAnalyticsReportCommand request, CancellationToken cancellationToken)
    {
        var deleted = await db.AnalyticsReports.Where(r => r.Id == request.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (deleted == 0)
        {
            throw new NotFoundException("analytics_report", request.Id);
        }

        changes.Publish(AnalyticsRealtime.Report, request.Id, RealtimeChangeKind.Deleted);
        return Unit.Value;
    }

    /// <summary>
    /// 按给定顺序重排。没列出的报表排在后面、保持原有先后；不认识的 ID 忽略。
    /// </summary>
    public async Task<IReadOnlyList<AnalyticsReportDto>> HandleAsync(ReorderAnalyticsReportsCommand request, CancellationToken cancellationToken)
    {
        var reports = await db.AnalyticsReports.OrderBy(r => r.Position).ThenBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var order = request.Ids.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var sorted = reports.OrderBy(r => order.TryGetValue(r.Id, out var index) ? index : int.MaxValue).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].Position != i)
            {
                sorted[i].Position = i;
                changes.Publish(AnalyticsRealtime.Report, sorted[i].Id, RealtimeChangeKind.Updated);
            }
        }

        return await ToDtosAsync(sorted, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 保存的报表只能引用保存者自己看得到的指标和维度，免得替别人建出看不了的报表时毫无提示。
    /// </summary>
    private async Task EnsureMetricAsync(SaveAnalyticsReportRequest request, CancellationToken cancellationToken)
    {
        var metric = await catalog.RequireAsync(request.Metric.Trim(), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.Breakdown))
        {
            return;
        }

        var dimensions = await catalog.DimensionsAsync(metric, cancellationToken).ConfigureAwait(false);
        if (dimensions.All(d => d.Key != request.Breakdown.Trim()))
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>
            {
                ["breakdown"] = [dimensions.Count == 0 ? $"「{metric.Name}」不支持拆分。" : $"「{metric.Name}」可以按这些维度拆分：{string.Join("、", dimensions.Select(d => d.Name))}。"],
            });
        }
    }

    private static void Apply(AnalyticsReport report, SaveAnalyticsReportRequest r)
    {
        report.Description = r.Description.Trim();
        report.Breakdown = string.IsNullOrWhiteSpace(r.Breakdown) ? null : r.Breakdown.Trim();
        report.Currency = string.IsNullOrWhiteSpace(r.Currency) ? null : r.Currency.Trim().ToUpperInvariant();
        report.Range = r.Range;
        report.Interval = r.Interval;
        report.Chart = r.Chart;
    }

    private async Task<IReadOnlyList<AnalyticsReportDto>> ToDtosAsync(IReadOnlyList<AnalyticsReport> reports, CancellationToken cancellationToken)
    {
        var names = await ActorNames.ResolveAsync(db, reports.Select(r => r.CreatedBy), cancellationToken).ConfigureAwait(false);
        return [.. reports.Select(r => new AnalyticsReportDto(
            r.Id, r.Name, r.Description, r.Metric, r.Breakdown, r.Currency, r.Range, r.Interval, r.Chart, r.Position,
            names(r.CreatedBy), r.CreatedAt, r.UpdatedAt))];
    }
}
