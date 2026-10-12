using System.Linq.Expressions;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Application.Modules;
using AiNative.Application.Tenancy;
using AiNative.Contracts.Analytics;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Analytics;

/// <summary>
/// 拆分维度。RequiredFeature 不为空时，查看者还要有这个权限才能按它拆分（例如按项目拆分任务要能看项目），
/// 维度所属的模块没启用时也不可用。
/// </summary>
public sealed record MetricDimension(string Key, string Name, string? RequiredFeature = null, string? ModuleKey = null, string EmptyLabel = "未指定");

/// <summary>
/// 一次采样的范围：起止时刻（UTC，左闭右开）、按哪个时区分天、按哪个维度分组。
/// </summary>
public sealed record MetricScope(DateTimeOffset From, DateTimeOffset To, string TimeZone, string? Breakdown);

/// <summary>
/// 某天（统计时区里的日期）某组的原始值。比率类指标 Value 是分子、Weight 是分母；
/// Group 为空字符串表示该记录没有这个维度的值（例如未指定负责人），不拆分时总是空字符串。
/// </summary>
public readonly record struct MetricSample(DateOnly Date, string Group, string? Currency, decimal Value, decimal Weight);

/// <summary>
/// 平台指标：一次定义，同时供经营分析界面、保存的报表、对话与智能体（analytics.query 动作）和 MCP 使用。
/// 实现类放进已注册的程序集即可被扫描；只负责从数据库按天取出原始值，分桶、拆分、对比由引擎统一处理。
/// </summary>
public interface IPlatformMetric
{
    /// <summary>
    /// 全局唯一键，形如 "crm.deals_won"。
    /// </summary>
    string Key { get; }

    string ModuleKey { get; }

    string Name { get; }

    /// <summary>
    /// 给人和模型看的口径说明：统计什么、按什么时间算。
    /// </summary>
    string Description { get; }

    string RequiredFeature { get; }

    MetricUnit Unit { get; }

    bool IsRatio { get; }

    IReadOnlyList<MetricDimension> Dimensions { get; }

    Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// 把维度值（通常是 ID）换成显示名称。找不到的键可以不返回，界面显示为"已删除"。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}

public abstract class PlatformMetric : IPlatformMetric
{
    public abstract string Key { get; }

    public abstract string ModuleKey { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string RequiredFeature { get; }

    public abstract MetricUnit Unit { get; }

    public virtual bool IsRatio => false;

    public virtual IReadOnlyList<MetricDimension> Dimensions => [];

    public abstract Task<IReadOnlyList<MetricSample>> SampleAsync(MetricScope scope, CancellationToken cancellationToken);

    public virtual Task<IReadOnlyDictionary<string, string>> LabelsAsync(string dimension, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    /// <summary>
    /// 枚举类维度（状态、类型）的固定名称。
    /// </summary>
    protected static Task<IReadOnlyDictionary<string, string>> Fixed(IReadOnlyDictionary<string, string> names)
        => Task.FromResult(names);
}

/// <summary>
/// 采样的中间形状：各指标把自己的记录投影成它（时间、分组、货币、数值），再统一按统计时区分天汇总，
/// 这样每个指标只写一条查询，聚合都在数据库里完成。
/// </summary>
public sealed class MetricRow
{
    public DateTimeOffset At { get; init; }

    public string Group { get; init; } = string.Empty;

    public string? Currency { get; init; }

    public decimal Value { get; init; }

    public decimal Weight { get; init; }
}

public static class MetricSampling
{
    /// <summary>
    /// 按 (统计时区里的日期, 分组, 货币) 汇总。转换成 <c>date_trunc('day', at AT TIME ZONE tz)</c>，在数据库里完成。
    /// </summary>
    public static async Task<IReadOnlyList<MetricSample>> ByDayAsync(this IQueryable<MetricRow> rows, string timeZone, CancellationToken cancellationToken)
    {
        rows = rows.Provider.CreateQuery<MetricRow>(new CompleteRows().Visit(rows.Expression));
        var groups = await rows
            .GroupBy(r => new { Day = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(r.At.UtcDateTime, timeZone).Date, r.Group, r.Currency })
            .Select(g => new { g.Key.Day, g.Key.Group, g.Key.Currency, Value = g.Sum(r => r.Value), Weight = g.Sum(r => r.Weight) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. groups.Select(g => new MetricSample(DateOnly.FromDateTime(g.Day), g.Group, g.Currency?.Trim(), g.Value, g.Weight))];
    }

    /// <summary>
    /// 给投影里没写的成员补上默认值（不分组、无货币、数值和权重为 0）。
    /// EF 无法翻译对未赋值成员的访问，补齐后各指标只需写出自己用到的成员。
    /// </summary>
    private sealed class CompleteRows : ExpressionVisitor
    {
        private static readonly (string Name, Expression Value)[] Defaults =
        [
            (nameof(MetricRow.Group), Expression.Constant(string.Empty)),
            (nameof(MetricRow.Currency), Expression.Constant(null, typeof(string))),
            (nameof(MetricRow.Value), Expression.Constant(0m)),
            (nameof(MetricRow.Weight), Expression.Constant(0m)),
        ];

        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            var visited = (MemberInitExpression)base.VisitMemberInit(node);
            if (visited.Type != typeof(MetricRow))
            {
                return visited;
            }

            var missing = Defaults.Where(d => visited.Bindings.All(b => b.Member.Name != d.Name))
                .Select(d => Expression.Bind(typeof(MetricRow).GetProperty(d.Name)!, d.Value));
            return visited.Update(visited.NewExpression, [.. visited.Bindings, .. missing]);
        }
    }
}

/// <summary>
/// 常用维度值的名称查找（成员、智能体、公司、产品、项目、工作流）。只查当前空间的数据。
/// </summary>
public sealed class MetricLabels(IAppDbContext db)
{
    public async Task<IReadOnlyDictionary<string, string>> UsersAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        var users = await db.Memberships.AsNoTracking().Where(m => ids.Contains(m.UserId))
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return users.DistinctBy(u => u.Id).ToDictionary(u => u.Id.ToString(), u => u.DisplayName);
    }

    public async Task<IReadOnlyDictionary<string, string>> AgentsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        return await db.Agents.AsNoTracking().Where(a => ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id.ToString(), a => a.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 成员或智能体（花费、跟进记录的作者既可能是人也可能是智能体）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> ActorsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var users = await UsersAsync(keys, cancellationToken).ConfigureAwait(false);
        var agents = await AgentsAsync(keys, cancellationToken).ConfigureAwait(false);
        var names = new Dictionary<string, string>(users);
        foreach (var (key, name) in agents)
        {
            names[key] = $"{name}（智能体）";
        }

        return names;
    }

    public async Task<IReadOnlyDictionary<string, string>> CompaniesAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        return await db.CrmCompanies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id.ToString(), c => c.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ProductsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        return await db.ErpProducts.AsNoTracking().Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id.ToString(), p => $"{p.Sku} {p.Name}", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> ProjectsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        return await db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id.ToString(), p => p.Name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 工作流已删除时用最近一次运行记下的名称。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WorkflowsAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var ids = Ids(keys);
        var names = await db.Workflows.AsNoTracking().Where(w => ids.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id.ToString(), w => w.Name, cancellationToken).ConfigureAwait(false);
        var missing = ids.Where(id => !names.ContainsKey(id.ToString())).ToList();
        if (missing.Count > 0)
        {
            var deleted = await db.WorkflowRuns.AsNoTracking().Where(r => missing.Contains(r.WorkflowId))
                .GroupBy(r => r.WorkflowId)
                .Select(g => new { Id = g.Key, Name = g.OrderByDescending(r => r.CreatedAt).Select(r => r.WorkflowName).First() })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var workflow in deleted)
            {
                names[workflow.Id.ToString()] = $"{workflow.Name}（已删除）";
            }
        }

        return names;
    }

    private static List<Guid> Ids(IReadOnlyCollection<string> keys)
        => [.. keys.Select(k => Guid.TryParse(k, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct()];
}

/// <summary>
/// 当前身份可用的指标：所属模块已启用、有指标要求的权限。
/// </summary>
public sealed class MetricCatalog(IEnumerable<IPlatformMetric> metrics, ITenantContext tenant, IModuleAccess modules)
{
    private readonly IReadOnlyList<IPlatformMetric> _metrics = [.. metrics.OrderBy(m => m.Key, StringComparer.Ordinal)];

    public IReadOnlyList<IPlatformMetric> All => _metrics;

    public IPlatformMetric? Find(string key) => _metrics.FirstOrDefault(m => m.Key == key);

    public async Task<IReadOnlyList<IPlatformMetric>> AvailableAsync(CancellationToken cancellationToken)
    {
        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        return [.. _metrics.Where(m => Visible(m, enabled))];
    }

    /// <summary>
    /// 取一个可用的指标：不存在返回 404，没权限或模块没启用返回 403。
    /// </summary>
    public async Task<IPlatformMetric> RequireAsync(string key, CancellationToken cancellationToken)
    {
        var metric = Find(key) ?? throw new NotFoundException("metric", key);
        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        return Visible(metric, enabled) ? metric : throw new ForbiddenException($"没有查看「{metric.Name}」的权限，或它所属的模块没有启用。");
    }

    public async Task<IReadOnlyList<MetricDimension>> DimensionsAsync(IPlatformMetric metric, CancellationToken cancellationToken)
    {
        var enabled = await modules.GetEnabledAsync(cancellationToken).ConfigureAwait(false);
        return [.. metric.Dimensions.Where(d => DimensionVisible(d, enabled))];
    }

    private bool Visible(IPlatformMetric metric, IReadOnlySet<string> enabled)
        => enabled.Contains(metric.ModuleKey) && tenant.Can(metric.RequiredFeature);

    private bool DimensionVisible(MetricDimension dimension, IReadOnlySet<string> enabled)
        => (dimension.ModuleKey is null || enabled.Contains(dimension.ModuleKey))
           && (dimension.RequiredFeature is null || tenant.Can(dimension.RequiredFeature));
}
