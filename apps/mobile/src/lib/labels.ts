import type {
  ApprovalStatus,
  WorkItemPriority,
  WorkItemStatus,
  WorkflowRunStatus,
  WorkflowStepKind,
  WorkflowStepStatus,
  WorkflowTriggerKind,
} from '@ainative/api-client';

export const workItemStatus: Record<WorkItemStatus, string> = {
  backlog: '待规划',
  todo: '待办',
  in_progress: '进行中',
  in_review: '待验收',
  blocked: '受阻',
  done: '已完成',
  cancelled: '已取消',
};

export const priority: Record<WorkItemPriority, string> = {
  low: '低',
  medium: '中',
  high: '高',
  urgent: '紧急',
};

export const approvalStatus: Record<ApprovalStatus, string> = {
  pending: '待审批',
  approved: '已批准',
  rejected: '已驳回',
};

export const toolStatus: Record<string, string> = {
  succeeded: '已完成',
  pending_approval: '待审批',
  failed: '失败',
  forbidden: '无权限',
  not_found: '不存在',
  invalid_input: '参数有误',
};

export const workflowRunStatus: Record<WorkflowRunStatus, string> = {
  queued: '排队中',
  running: '运行中',
  waiting: '等待中',
  succeeded: '成功',
  failed: '失败',
  cancelled: '已取消',
};

export const workflowStepStatus: Record<WorkflowStepStatus, string> = {
  running: '执行中',
  waiting: '等待中',
  succeeded: '成功',
  failed: '失败',
  skipped: '已跳过',
};

export const workflowTriggerKind: Record<WorkflowTriggerKind, string> = {
  manual: '手动运行',
  event: '事件触发',
  schedule: '定时运行',
};

export const workflowStepKind: Record<WorkflowStepKind, string> = {
  action: '执行动作',
  agent_task: '派给智能体',
  llm: '模型处理',
  delay: '等待',
};

export function formatTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}
