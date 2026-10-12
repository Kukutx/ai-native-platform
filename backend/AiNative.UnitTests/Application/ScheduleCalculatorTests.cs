using AiNative.Application.Work;

namespace AiNative.UnitTests.Application;

public sealed class ScheduleCalculatorTests
{
    private static readonly DateOnly Day0 = new(2026, 11, 1);
    private static readonly Guid A = Guid.CreateVersion7();
    private static readonly Guid B = Guid.CreateVersion7();
    private static readonly Guid C = Guid.CreateVersion7();
    private static readonly Guid D = Guid.CreateVersion7();
    private static readonly Guid E = Guid.CreateVersion7();

    private static DateOnly Day(int offset) => Day0.AddDays(offset);

    private static ScheduleNode Node(Guid id, int? start, int? due, bool closed = false)
        => new(id, start is { } s ? Day(s) : null, due is { } d ? Day(d) : null, closed);

    [Fact]
    public void Successor_may_start_on_the_day_its_predecessor_is_due()
    {
        ScheduleCalculator.IsConflict(Node(A, 1, 3), Node(B, 3, 5)).Should().BeFalse();
        ScheduleCalculator.IsConflict(Node(A, 1, 3), Node(B, 2, 5)).Should().BeTrue();

        // 只有截止日期的任务按截止当天算；关闭的或没有日期的不算冲突。
        ScheduleCalculator.IsConflict(Node(A, null, 4), Node(B, null, 3)).Should().BeTrue();
        ScheduleCalculator.IsConflict(Node(A, 1, 3, closed: true), Node(B, 2, 5)).Should().BeFalse();
        ScheduleCalculator.IsConflict(Node(A, 1, 3), Node(B, null, null)).Should().BeFalse();
    }

    [Fact]
    public void Shifting_moves_only_dependents_that_now_conflict_and_follows_the_chain()
    {
        // A → B → C，A → D；D 另外依赖不相关的 E（E 截止得更晚，原本就冲突）。
        var nodes = new Dictionary<Guid, ScheduleNode>
        {
            [B] = Node(B, 4, 6),
            [C] = Node(C, 7, 8),
            [D] = Node(D, 4, 10),
        };
        ScheduleEdge[] edges = [new(B, A), new(C, B), new(D, A)];

        var plan = ScheduleCalculator.PlanShift(Node(A, 1, 5), nodes, edges);

        // B 从第 4 天挪到第 5 天（A 的新截止），C 第 7 天开始仍不早于 B 的新截止第 7 天，不动；D 同样挪一天。
        plan.Should().BeEquivalentTo(new[]
        {
            new ScheduleShift(B, Day(5), Day(7), 1),
            new ScheduleShift(D, Day(5), Day(11), 1),
        });
    }

    [Fact]
    public void Shifting_skips_closed_and_undated_tasks_and_never_pulls_earlier()
    {
        var nodes = new Dictionary<Guid, ScheduleNode>
        {
            [B] = Node(B, 2, 3, closed: true),
            [C] = Node(C, null, null),
            [D] = Node(D, null, 4),
        };
        ScheduleEdge[] edges = [new(B, A), new(C, A), new(D, A)];

        // 只有截止日期的 D 整体按截止那天挪。
        ScheduleCalculator.PlanShift(Node(A, 1, 6), nodes, edges).Should().Equal(new ScheduleShift(D, null, Day(6), 2));

        // A 提前不会把后续任务往前拉；关闭的根任务不推动任何人。
        ScheduleCalculator.PlanShift(Node(A, 0, 1), nodes, edges).Should().BeEmpty();
        ScheduleCalculator.PlanShift(Node(A, 1, 9, closed: true), nodes, edges).Should().BeEmpty();
    }

    [Fact]
    public void Shifts_propagate_through_moved_tasks_only()
    {
        // A → B → C；E → C，E 截止第 20 天与 C 原本冲突，但这次调整与 E 无关，C 只按 B 的新截止挪。
        var nodes = new Dictionary<Guid, ScheduleNode>
        {
            [B] = Node(B, 3, 4),
            [C] = Node(C, 5, 6),
            [E] = Node(E, 1, 20),
        };
        ScheduleEdge[] edges = [new(B, A), new(C, B), new(C, E)];

        ScheduleCalculator.PlanShift(Node(A, 1, 6), nodes, edges).Should().Equal(
            new ScheduleShift(B, Day(6), Day(7), 3),
            new ScheduleShift(C, Day(7), Day(8), 2));
    }

    [Fact]
    public void Critical_path_is_the_chain_without_slack()
    {
        // A(1–5) → B(5–7) → C(7–8)，A → D(5–11)。终点第 11 天由 D 决定：A、D 没有余量，B、C 各有 3 天余量。
        ScheduleNode[] nodes = [Node(A, 1, 5), Node(B, 5, 7), Node(C, 7, 8), Node(D, 5, 11), Node(E, null, null)];
        ScheduleEdge[] edges = [new(B, A), new(C, B), new(D, A)];

        ScheduleCalculator.CriticalPath(nodes, edges).Should().BeEquivalentTo([A, D]);
    }

    [Fact]
    public void Critical_path_ignores_closed_tasks_and_flags_conflicting_chains()
    {
        // A 已完成，不在关键路径上；B(2–9) → C(4–10) 冲突：C 决定终点，B 的余量为负，两者都关键。
        ScheduleNode[] nodes = [Node(A, 1, 12, closed: true), Node(B, 2, 9), Node(C, 4, 10), Node(D, 1, 3)];
        ScheduleEdge[] edges = [new(B, A), new(C, B)];

        ScheduleCalculator.CriticalPath(nodes, edges).Should().BeEquivalentTo([B, C]);
        ScheduleCalculator.CriticalPath([], []).Should().BeEmpty();
    }
}
