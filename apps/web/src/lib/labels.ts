import type {
  AgentStatus,
  ApprovalStatus,
  MemoryScope,
  RunStatus,
  WakeupSource,
  WorkItemPriority,
  WorkItemStatus,
  WorkflowRunStatus,
  WorkflowStepKind,
  WorkflowStepStatus,
  WorkflowTriggerKind,
} from '@ainative/api-client';

type Tone = 'default' | 'secondary' | 'destructive' | 'outline';

export const agentStatus: Record<AgentStatus, { label: string; tone: Tone }> = {
  idle: { label: '空闲', tone: 'secondary' },
  running: { label: '工作中', tone: 'default' },
  paused: { label: '已暂停', tone: 'outline' },
  error: { label: '出错', tone: 'destructive' },
  terminated: { label: '已终止', tone: 'outline' },
};

export const workItemStatus: Record<WorkItemStatus, { label: string; tone: Tone }> = {
  backlog: { label: '待规划', tone: 'outline' },
  todo: { label: '待办', tone: 'secondary' },
  in_progress: { label: '进行中', tone: 'default' },
  in_review: { label: '待验收', tone: 'default' },
  blocked: { label: '受阻', tone: 'destructive' },
  done: { label: '已完成', tone: 'secondary' },
  cancelled: { label: '已取消', tone: 'outline' },
};

export const boardColumns: WorkItemStatus[] = ['backlog', 'todo', 'in_progress', 'in_review', 'blocked', 'done'];

export const priority: Record<WorkItemPriority, string> = {
  low: '低',
  medium: '中',
  high: '高',
  urgent: '紧急',
};

export const runStatus: Record<RunStatus, { label: string; tone: Tone }> = {
  queued: { label: '排队中', tone: 'outline' },
  running: { label: '执行中', tone: 'default' },
  succeeded: { label: '成功', tone: 'secondary' },
  failed: { label: '失败', tone: 'destructive' },
  cancelled: { label: '已取消', tone: 'outline' },
  timed_out: { label: '超时', tone: 'destructive' },
};

export const approvalStatus: Record<ApprovalStatus, { label: string; tone: Tone }> = {
  pending: { label: '待审批', tone: 'default' },
  approved: { label: '已批准', tone: 'secondary' },
  rejected: { label: '已驳回', tone: 'destructive' },
};

export const wakeupSource: Record<WakeupSource, string> = {
  on_demand: '手动唤醒',
  assignment: '任务指派',
  comment: '新评论',
  approval: '审批结果',
  timer: '定时心跳',
  delegation: '同事委派',
  automation: '自动化',
};

export const memoryScope: Record<MemoryScope, { label: string; tone: Tone }> = {
  tenant: { label: '共享', tone: 'default' },
  user: { label: '成员', tone: 'secondary' },
  agent: { label: '智能体', tone: 'outline' },
};

export const workflowRunStatus: Record<WorkflowRunStatus, { label: string; tone: Tone }> = {
  queued: { label: '排队中', tone: 'outline' },
  running: { label: '运行中', tone: 'default' },
  waiting: { label: '等待中', tone: 'default' },
  succeeded: { label: '成功', tone: 'secondary' },
  failed: { label: '失败', tone: 'destructive' },
  cancelled: { label: '已取消', tone: 'outline' },
};

export const workflowStepStatus: Record<WorkflowStepStatus, { label: string; tone: Tone }> = {
  running: { label: '执行中', tone: 'default' },
  waiting: { label: '等待中', tone: 'default' },
  succeeded: { label: '成功', tone: 'secondary' },
  failed: { label: '失败', tone: 'destructive' },
  skipped: { label: '已跳过', tone: 'outline' },
};

export const workflowTriggerKind: Record<WorkflowTriggerKind, string> = {
  manual: '手动运行',
  event: '事件触发',
  schedule: '定时运行',
};

export const workflowStepKind: Record<WorkflowStepKind, { label: string; hint: string }> = {
  action: { label: '执行动作', hint: '调用平台动作，例如创建任务、写评论。' },
  agent_task: { label: '派给智能体', hint: '建一个任务交给智能体，可以等它完成后再继续。' },
  llm: { label: '模型处理', hint: '让模型总结、分类或改写文本，内容是 JSON 时可以直接取字段。' },
  delay: { label: '等待', hint: '暂停一段时间再继续。' },
};

export function formatTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}

export function formatUsd(value: number) {
  return `$${value.toFixed(value < 1 ? 4 : 2)}`;
}
