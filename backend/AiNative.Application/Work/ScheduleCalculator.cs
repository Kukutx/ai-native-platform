namespace AiNative.Application.Work;

/// <summary>
/// 排期计算用到的任务信息。
/// </summary>
internal readonly record struct ScheduleNode(Guid Id, DateOnly? Start, DateOnly? Due, bool Closed)
{
    /// <summary>
    /// 时间条覆盖的日期（含首尾）。只有截止日期或只有开始日期时按那一天算；都没有时为 null。
    /// </summary>
    public (DateOnly Start, DateOnly End)? Span
        => (Start ?? Due, Due ?? Start) is ({ } start, { } end) ? (start, end) : null;
}

/// <summary>
/// 依赖边：<see cref="Successor"/> 要等 <see cref="Predecessor"/> 关闭后才能开始。
/// </summary>
public readonly record struct ScheduleEdge(Guid Successor, Guid Predecessor);

/// <param name="Days">往后挪的天数。</param>
internal readonly record struct ScheduleShift(Guid Id, DateOnly? Start, DateOnly? Due, int Days);

/// <summary>
/// 排期的纯计算：冲突判断、顺延后续任务、关键路径。依赖按"完成后才能开始"理解，日期精确到天，
/// 后续任务可以在前置任务截止的当天开始。
/// </summary>
internal static class ScheduleCalculator
{
    /// <summary>
    /// 两个任务都未关闭、都有日期，后续任务的开始早于前置任务的截止时算冲突。
    /// </summary>
    public static bool IsConflict(ScheduleNode predecessor, ScheduleNode successor)
        => !predecessor.Closed && !successor.Closed
           && predecessor.Span is { } before && successor.Span is { } after
           && after.Start < before.End;

    /// <summary>
    /// 顺延计划：沿依赖链往下，只要某个任务的开始早于"这次调整过的"前置任务的截止，就把它的开始和截止一起往后挪到不冲突，
    /// 挪过的任务再影响它的后续任务。根任务按调整后的日期计算（已关闭时不推动别人）；已关闭、没有日期的任务不动；只往后挪，不会提前。
    /// </summary>
    /// <param name="root">被调整的任务（日期已是新值）。</param>
    /// <param name="nodes">根任务的全部下游任务。</param>
    /// <param name="edges">下游任务之间（以及指向根任务）的依赖。</param>
    public static IReadOnlyList<ScheduleShift> PlanShift(ScheduleNode root, IReadOnlyDictionary<Guid, ScheduleNode> nodes, IReadOnlyCollection<ScheduleEdge> edges)
    {
        var current = new Dictionary<Guid, ScheduleNode>(nodes) { [root.Id] = root };
        var predecessors = edges.ToLookup(e => e.Successor, e => e.Predecessor);
        var changed = new HashSet<Guid> { root.Id };
        var shifts = new List<ScheduleShift>();
        foreach (var id in TopologicalOrder(current.Keys, edges))
        {
            if (id == root.Id || current[id] is not { Closed: false, Span: { } span } node)
            {
                continue;
            }

            DateOnly? required = null;
            foreach (var predecessor in predecessors[id])
            {
                if (changed.Contains(predecessor) && current.TryGetValue(predecessor, out var before) && !before.Closed && before.Span is { } then
                    && (required is null || then.End > required))
                {
                    required = then.End;
                }
            }

            if (required is not { } earliest || span.Start >= earliest)
            {
                continue;
            }

            var days = earliest.DayNumber - span.Start.DayNumber;
            var moved = node with { Start = node.Start?.AddDays(days), Due = node.Due?.AddDays(days) };
            current[id] = moved;
            changed.Add(id);
            shifts.Add(new ScheduleShift(id, moved.Start, moved.Due, days));
        }

        return shifts;
    }

    /// <summary>
    /// 关键路径：只看未关闭、有日期的任务。项目终点取它们最晚的截止日期，从后往前推每个任务的最晚截止，
    /// 没有余量（晚一天终点就跟着晚）的任务在关键路径上。已有冲突的链条余量为负，同样算关键。
    /// </summary>
    public static IReadOnlySet<Guid> CriticalPath(IReadOnlyCollection<ScheduleNode> nodes, IReadOnlyCollection<ScheduleEdge> edges)
    {
        var spans = nodes.Where(n => !n.Closed && n.Span is not null).ToDictionary(n => n.Id, n => n.Span!.Value);
        if (spans.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var end = spans.Values.Max(s => s.End);
        var relevant = edges.Where(e => spans.ContainsKey(e.Successor) && spans.ContainsKey(e.Predecessor)).ToList();
        var successors = relevant.ToLookup(e => e.Predecessor, e => e.Successor);
        var latestStart = new Dictionary<Guid, int>();
        var critical = new HashSet<Guid>();
        foreach (var id in TopologicalOrder(spans.Keys, relevant).Reverse())
        {
            var span = spans[id];
            var next = successors[id].Where(latestStart.ContainsKey).Select(s => latestStart[s]).DefaultIfEmpty(end.DayNumber).Min();
            latestStart[id] = next - (span.End.DayNumber - span.Start.DayNumber);
            if (next <= span.End.DayNumber)
            {
                critical.Add(id);
            }
        }

        return critical;
    }

    /// <summary>
    /// 拓扑排序（前置在前）。依赖关系保证无环；万一有环，环上的任务不出现在结果里。
    /// </summary>
    private static IEnumerable<Guid> TopologicalOrder(IEnumerable<Guid> ids, IReadOnlyCollection<ScheduleEdge> edges)
    {
        var pending = ids.ToDictionary(id => id, _ => 0);
        foreach (var edge in edges)
        {
            if (pending.ContainsKey(edge.Successor) && pending.ContainsKey(edge.Predecessor))
            {
                pending[edge.Successor]++;
            }
        }

        var successors = edges.Where(e => pending.ContainsKey(e.Successor) && pending.ContainsKey(e.Predecessor)).ToLookup(e => e.Predecessor, e => e.Successor);
        var ready = new Queue<Guid>(pending.Where(p => p.Value == 0).Select(p => p.Key).Order());
        var ordered = new List<Guid>(pending.Count);
        while (ready.TryDequeue(out var id))
        {
            ordered.Add(id);
            foreach (var next in successors[id])
            {
                if (--pending[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        return ordered;
    }
}
