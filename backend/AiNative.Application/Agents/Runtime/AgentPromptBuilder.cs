using System.Globalization;
using System.Text;
using AiNative.Application.Abstractions;
using AiNative.Application.Access;
using AiNative.Application.Analytics;
using AiNative.Application.Common;
using AiNative.Application.Knowledge;
using AiNative.Application.Modules;
using AiNative.Application.Projects;
using AiNative.Application.Tenancy;
using AiNative.Domain.Agents;
using AiNative.Domain.Governance;
using AiNative.Domain.Knowledge;
using AiNative.Domain.Projects;
using AiNative.Domain.Work;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AiNative.Application.Agents.Runtime;

/// <summary>
/// 组装一轮心跳的上下文：身份与组织关系、唤醒原因、聚焦任务、自己的待办和待审批事项，
/// 以及按本轮内容自动回忆的长期记忆和知识库资料。每轮都从数据库现取，不依赖上一轮的对话。
/// </summary>
internal sealed class AgentPromptBuilder(
    IAppDbContext db,
    IKnowledgeSearch knowledge,
    IModuleAccess modules,
    TimeProvider clock,
    ILogger<AgentPromptBuilder> logger)
{
    private const int RecalledMemories = 6;
    private const int RecalledDocuments = 3;

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
        if (FeatureMatcher.IsGranted(agent.Permissions, Features.Memory.Use))
        {
            sb.AppendLine("- 你每轮都会重新开始，只有记下来的东西才会留到以后：学到以后还会用到的偏好、约定、事实或教训，用 memory_retain 记下（团队都该知道的设 shared）。");
        }

        if (FeatureMatcher.IsGranted(agent.Permissions, Features.Knowledge.View))
        {
            sb.AppendLine("- 需要背景资料时先用 knowledge_search 查团队知识库，引用时注明文档标题。");
        }

        if (FeatureMatcher.IsGranted(agent.Permissions, ProjectFeatures.View)
            && await modules.IsEnabledAsync(ProjectsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            sb.AppendLine("- 任务可以归入项目和里程碑并设截止日期（tasks_create 带 projectId、milestoneId、dueDate，已有任务用 tasks_schedule 调整）。负责项目时用里程碑组织工作，里程碑的任务都关闭后验收并标记完成。");
        }

        if (FeatureMatcher.IsGranted(agent.Permissions, AnalyticsFeatures.View)
            && await modules.IsEnabledAsync(AnalyticsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            sb.AppendLine("- 涉及经营数据（销售额、赢单、任务完成量、花费等）时用 analytics_query 或 analytics_get_overview 查实际数字，不要估算；写周报月报先看概览。");
        }

        return sb.ToString();
    }

    public async Task<string> BuildWakeMessageAsync(Agent agent, WakeupRequest? wakeup, WorkItem? focus, bool checkedOut, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var timeZone = await db.Tenants.AsNoTracking().Where(t => t.Id == agent.TenantId).Select(t => t.TimeZone)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        sb.AppendLine(CultureInfo.InvariantCulture, $"现在是 {TimeZones.Describe(clock.GetUtcNow(), timeZone)}，你被唤醒了。");
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

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var projectIds = mine.Where(w => w.ProjectId != null).Select(w => w.ProjectId!.Value).Distinct().ToList();
        var projectNames = projectIds.Count == 0
            ? []
            : await db.Projects.AsNoTracking().Where(p => projectIds.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken).ConfigureAwait(false);
        foreach (var w in mine)
        {
            var project = w.ProjectId is { } projectId && projectNames.TryGetValue(projectId, out var name) ? $"，项目《{name}》" : string.Empty;
            sb.AppendLine(CultureInfo.InvariantCulture, $"- T-{w.Number}《{w.Title}》 状态 {w.Status}，优先级 {w.Priority}{project}{DueText(w.DueDate, today)}，id {w.Id}");
        }

        await AppendLedProjectsAsync(sb, agent, today, cancellationToken).ConfigureAwait(false);

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

        await AppendRecallAsync(sb, agent, wakeup, focus, cancellationToken).ConfigureAwait(false);

        sb.AppendLine().AppendLine("请处理本轮需要做的事。");
        return sb.ToString();
    }

    /// <summary>
    /// 自动回忆：用唤醒原因和聚焦任务做查询，带入最相关的记忆与知识库片段。检索失败不影响本轮执行。
    /// </summary>
    private async Task AppendRecallAsync(StringBuilder sb, Agent agent, WakeupRequest? wakeup, WorkItem? focus, CancellationToken cancellationToken)
    {
        var useMemory = FeatureMatcher.IsGranted(agent.Permissions, Features.Memory.Use);
        var useKnowledge = FeatureMatcher.IsGranted(agent.Permissions, Features.Knowledge.View);
        if ((!useMemory && !useKnowledge) || !await modules.IsEnabledAsync(KnowledgeModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var query = new StringBuilder();
        if (focus is not null)
        {
            query.AppendLine(focus.Title).AppendLine(Truncate(focus.Description, 500));
        }

        foreach (var item in wakeup?.Items ?? [])
        {
            query.AppendLine(item.Reason);
        }

        if (query.Length == 0)
        {
            query.Append(agent.Title).Append(' ').Append(Truncate(agent.Instructions ?? string.Empty, 300));
        }

        try
        {
            if (useMemory)
            {
                var memories = await knowledge.SearchAsync(new KnowledgeSearchRequest
                {
                    Query = query.ToString(),
                    IncludeDocuments = false,
                    Limit = RecalledMemories,
                }, cancellationToken).ConfigureAwait(false);
                if (memories.Count > 0)
                {
                    var ids = memories.Select(m => m.SourceId).ToList();
                    var details = await db.MemoryEntries.AsNoTracking().Where(m => ids.Contains(m.Id))
                        .Select(m => new { m.Id, m.Scope, m.CreatedAt })
                        .ToDictionaryAsync(m => m.Id, cancellationToken).ConfigureAwait(false);
                    sb.AppendLine().AppendLine("## 相关记忆（按本轮内容自动回忆，可能不完整）");
                    foreach (var memory in memories.Where(m => details.ContainsKey(m.SourceId)))
                    {
                        var detail = details[memory.SourceId];
                        var label = detail.Scope == MemoryScope.Tenant ? "团队" : "我的";
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- [{label}，{detail.CreatedAt:yyyy-MM-dd}] {Truncate(memory.Content, 500)}（记忆 {memory.SourceId}）");
                    }
                }
            }

            if (useKnowledge)
            {
                var documents = await knowledge.SearchAsync(new KnowledgeSearchRequest
                {
                    Query = query.ToString(),
                    IncludeMemories = false,
                    Limit = RecalledDocuments,
                }, cancellationToken).ConfigureAwait(false);
                if (documents.Count > 0)
                {
                    var ids = documents.Select(d => d.SourceId).Distinct().ToList();
                    var titles = await db.KnowledgeDocuments.AsNoTracking().Where(d => ids.Contains(d.Id))
                        .ToDictionaryAsync(d => d.Id, d => d.Title, cancellationToken).ConfigureAwait(false);
                    sb.AppendLine().AppendLine("## 知识库里可能相关的资料（需要全文用 knowledge_get_document）");
                    foreach (var hit in documents.Where(d => titles.ContainsKey(d.SourceId)))
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"- 《{titles[hit.SourceId]}》（文档 {hit.SourceId}）：{Truncate(hit.Content.ReplaceLineEndings(" "), 300)}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Automatic recall failed for agent {AgentId}", agent.Id);
        }
    }

    /// <summary>
    /// 智能体负责的未结束项目，带进度，方便它主动推进。
    /// </summary>
    private async Task AppendLedProjectsAsync(StringBuilder sb, Agent agent, DateOnly today, CancellationToken cancellationToken)
    {
        if (!FeatureMatcher.IsGranted(agent.Permissions, ProjectFeatures.View)
            || !await modules.IsEnabledAsync(ProjectsModule.ModuleKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var led = await db.Projects.AsNoTracking()
            .Where(p => p.LeadAgentId == agent.Id && p.Status != ProjectStatus.Completed && p.Status != ProjectStatus.Cancelled)
            .OrderBy(p => p.TargetDate == null).ThenBy(p => p.TargetDate).Take(10)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (led.Count == 0)
        {
            return;
        }

        sb.AppendLine().AppendLine("## 你负责的项目（详情用 projects_get）");
        foreach (var p in await ProjectMapping.ProjectsAsync(db, led, today, cancellationToken).ConfigureAwait(false))
        {
            var target = p.TargetDate is { } date ? $"，目标 {date:yyyy-MM-dd}{(p.IsOverdue ? "（已逾期）" : string.Empty)}" : string.Empty;
            var g = p.Progress;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 《{p.Name}》 状态 {p.Status}{target}，任务 {g.Done}/{g.Total} 已完成（进行中 {g.InProgress}，阻塞 {g.Blocked}，逾期 {g.Overdue}），id {p.Id}");
        }
    }

    private async Task AppendFocusAsync(StringBuilder sb, WorkItem focus, bool checkedOut, CancellationToken cancellationToken)
    {
        sb.AppendLine().AppendLine(checkedOut ? "## 本轮聚焦的任务（已为你领取）" : "## 相关任务（由他人负责，仅供参考）");
        sb.AppendLine(CultureInfo.InvariantCulture, $"{FocusMarker} {focus.Id}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"T-{focus.Number}《{focus.Title}》 状态 {focus.Status}，优先级 {focus.Priority}{DueText(focus.DueDate, DateOnly.FromDateTime(clock.GetLocalNow().DateTime))}");
        if (focus.ProjectId is { } projectId)
        {
            var project = await db.Projects.AsNoTracking().Where(p => p.Id == projectId)
                .Select(p => new { p.Name, p.Description, p.TargetDate }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var milestone = focus.MilestoneId is { } milestoneId
                ? await db.ProjectMilestones.AsNoTracking().Where(m => m.Id == milestoneId).Select(m => new { m.Name, m.DueDate })
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                : null;
            if (project is not null)
            {
                var stage = milestone is null ? string.Empty : $"，里程碑「{milestone.Name}」{(milestone.DueDate is { } due ? $"（{due:yyyy-MM-dd} 截止）" : string.Empty)}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"所属项目：《{project.Name}》{stage}（项目 id {projectId}）");
                if (!string.IsNullOrWhiteSpace(project.Description))
                {
                    sb.AppendLine("项目目标：").AppendLine(Truncate(project.Description, 1500));
                }
            }
        }

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

    private static string DueText(DateOnly? due, DateOnly today)
        => due is not { } date ? string.Empty : date < today ? $"，截止 {date:yyyy-MM-dd}（已逾期）" : $"，截止 {date:yyyy-MM-dd}";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…（已截断）";
}
