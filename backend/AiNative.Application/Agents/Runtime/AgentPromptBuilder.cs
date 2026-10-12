using System.Globalization;
using System.Text;
using AiNative.Application.Abstractions;
using AiNative.Application.Common;
using AiNative.Domain.Agents;
using AiNative.Domain.Governance;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;

namespace AiNative.Application.Agents.Runtime;

/// <summary>
/// 组装一轮心跳的上下文：身份与组织关系、唤醒原因、聚焦任务、自己的待办和待审批事项。
/// 每轮都从数据库现取，智能体不依赖上一轮的对话记忆。
/// </summary>
internal sealed class AgentPromptBuilder(IAppDbContext db, TimeProvider clock)
{
    public const string FocusMarker = "focus_task_id:";

    public async Task<string> BuildSystemPromptAsync(Agent agent, CancellationToken cancellationToken)
    {
        var tenantName = await db.Tenants.AsNoTracking().Where(t => t.Id == agent.TenantId).Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var team = await db.Agents.AsNoTracking().Where(a => a.Status != AgentStatus.Terminated)
            .Select(a => new { a.Id, a.Name, a.Title, a.Status, a.ReportsToId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var manager = team.FirstOrDefault(a => a.Id == agent.ReportsToId);
        var reports = team.Where(a => a.ReportsToId == agent.Id).ToList();

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"你是 {agent.Name}，在「{tenantName}」担任 {agent.Title}。你是平台上的智能体员工，通过工具完成工作。");
        if (!string.IsNullOrWhiteSpace(agent.Instructions))
        {
            sb.AppendLine().AppendLine("## 岗位职责").AppendLine(agent.Instructions.Trim());
        }

        sb.AppendLine().AppendLine("## 组织关系");
        sb.AppendLine(manager is null ? "- 上级：无（你直接对人类管理者负责）" : $"- 上级：{manager.Name}（{manager.Title}，id {manager.Id}）");
        if (reports.Count == 0)
        {
            sb.AppendLine("- 直接下属：无");
        }
        else
        {
            sb.AppendLine("- 直接下属：");
            foreach (var r in reports)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  - {r.Name}（{r.Title}，id {r.Id}，状态 {r.Status}）");
            }
        }

        sb.AppendLine().AppendLine("""
            ## 工作方式
            - 你按"心跳"工作：每次被唤醒执行一轮，处理完本轮需要做的事就结束，最后用两三句话总结做了什么。
            - 一切进展落在任务上：结论、交付物和问题写进任务评论（tasks_comment），完成后更新状态（done；需要人确认用 in_review；卡住用 blocked 并说明原因）。
            - 工作量大时拆成子任务（tasks_create 带 parentId），按岗位指派给下属；子任务完成时你会被唤醒来汇总。
            - 只做职责范围内、工具允许的事。缺信息就在任务评论里提问，不要编造。
            - 需要审批的操作提交一次即可，结果出来后你会被唤醒。
            - 评论和总结使用任务所用的语言。
            """);
        return sb.ToString();
    }

    public async Task<string> BuildWakeMessageAsync(Agent agent, WakeupRequest? wakeup, WorkItem? focus, bool checkedOut, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"现在是 {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC，你被唤醒了。");
        sb.AppendLine().AppendLine("## 唤醒原因");
        if (wakeup is null || wakeup.Items.Count == 0)
        {
            sb.AppendLine("- 例行检查：看看自己的待办有没有需要推进的。");
        }
        else
        {
            foreach (var item in wakeup.Items)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- [{item.Source}] {item.Reason}");
            }

            if (wakeup.CoalescedCount > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"（上次运行后累计了 {wakeup.CoalescedCount + 1} 次唤醒，已合并。）");
            }
        }

        if (focus is not null)
        {
            await AppendFocusAsync(sb, focus, checkedOut, cancellationToken).ConfigureAwait(false);
        }

        var mine = await db.WorkItems.AsNoTracking()
            .Where(w => w.AssigneeAgentId == agent.Id && w.Status != WorkItemStatus.Done && w.Status != WorkItemStatus.Cancelled)
            .OrderByDescending(w => w.Priority).ThenBy(w => w.Number)
            .Take(15)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        sb.AppendLine().AppendLine("## 你负责的未关闭任务");
        if (mine.Count == 0)
        {
            sb.AppendLine("- 无");
        }

        foreach (var w in mine)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- T-{w.Number}《{w.Title}》 状态 {w.Status}，优先级 {w.Priority}，id {w.Id}");
        }

        var pending = await db.Approvals.AsNoTracking()
            .Where(a => a.RequestedBy.Kind == Domain.Common.ActorKind.Agent && a.RequestedBy.Id == agent.Id && a.Status == ApprovalStatus.Pending)
            .OrderBy(a => a.CreatedAt).Take(10)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Count > 0)
        {
            sb.AppendLine().AppendLine("## 你提交、仍在等待审批的操作");
            foreach (var a in pending)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {a.ActionKey}（审批单 {a.Id}）");
            }
        }

        sb.AppendLine().AppendLine("请处理本轮需要做的事。");
        return sb.ToString();
    }

    private async Task AppendFocusAsync(StringBuilder sb, WorkItem focus, bool checkedOut, CancellationToken cancellationToken)
    {
        sb.AppendLine().AppendLine(checkedOut ? "## 本轮聚焦的任务（已为你领取）" : "## 相关任务（由他人负责，仅供参考）");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{FocusMarker} {focus.Id}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"T-{focus.Number}《{focus.Title}》 状态 {focus.Status}，优先级 {focus.Priority}");
        if (!string.IsNullOrWhiteSpace(focus.Description))
        {
            sb.AppendLine("描述：").AppendLine(Truncate(focus.Description, 6000));
        }

        if (focus.ParentId is { } parentId)
        {
            var parent = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == parentId, cancellationToken).ConfigureAwait(false);
            if (parent is not null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"上级任务：T-{parent.Number}《{parent.Title}》（id {parent.Id}）");
            }
        }

        var subtasks = await db.WorkItems.AsNoTracking().Where(w => w.ParentId == focus.Id).OrderBy(w => w.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (subtasks.Count > 0)
        {
            sb.AppendLine("子任务：");
            foreach (var s in subtasks)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- T-{s.Number}《{s.Title}》 状态 {s.Status}，负责智能体 {s.AssigneeAgentId?.ToString() ?? "无"}，id {s.Id}");
            }
        }

        var comments = await db.WorkItemComments.AsNoTracking().Where(c => c.WorkItemId == focus.Id)
            .OrderByDescending(c => c.CreatedAt).Take(15)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (comments.Count > 0)
        {
            var actors = await ActorNames.ResolveAsync(db, comments.Select(c => c.Author), cancellationToken).ConfigureAwait(false);
            sb.AppendLine("最近评论（旧→新）：");
            foreach (var c in comments.AsEnumerable().Reverse())
            {
                var author = actors(c.Author);
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {author.Name ?? author.Kind.ToString()}（{c.CreatedAt:MM-dd HH:mm}）：{Truncate(c.Body, 2000)}");
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…（已截断）";
}
