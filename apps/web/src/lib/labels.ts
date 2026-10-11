import type {
  AgentStatus,
  ApprovalStatus,
  RunStatus,
  WakeupSource,
  WorkItemPriority,
  WorkItemStatus,
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

export function formatTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}

export function formatUsd(value: number) {
  return `$${value.toFixed(value < 1 ? 4 : 2)}`;
}
