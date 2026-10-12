import type {
  AgentStatus,
  ApprovalStatus,
  CrmActivityKind,
  CrmDealStatus,
  CrmFieldType,
  CrmImportDuplicateMode,
  CrmRecordType,
  CrmStageKind,
  ErpPurchaseOrderStatus,
  ErpSalesOrderStatus,
  ErpStockDocumentKind,
  ErpStockMovementKind,
  McpApprovalMode,
  MemoryScope,
  ProjectStatus,
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

export const projectStatus: Record<ProjectStatus, { label: string; tone: Tone }> = {
  planned: { label: '计划中', tone: 'outline' },
  active: { label: '进行中', tone: 'default' },
  on_hold: { label: '暂停', tone: 'secondary' },
  completed: { label: '已完成', tone: 'secondary' },
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
  webhook: 'Webhook',
};

export const workflowStepKind: Record<WorkflowStepKind, { label: string; hint: string }> = {
  action: { label: '执行动作', hint: '调用平台动作，例如创建任务、写评论。' },
  agent_task: { label: '派给智能体', hint: '建一个任务交给智能体，可以等它完成后再继续。' },
  llm: { label: '模型处理', hint: '让模型总结、分类或改写文本，内容是 JSON 时可以直接取字段。' },
  delay: { label: '等待', hint: '暂停一段时间再继续。' },
  http: { label: 'HTTP 请求', hint: '调用外部接口，例如通知其他系统或查询外部数据。返回 JSON 时可以直接取字段，非 2xx 视为失败。' },
  branch: { label: '条件分支', hint: '按顺序判断各分支的条件，只执行第一个成立的分支。最后一个分支不写条件时表示其他情况。' },
  for_each: { label: '遍历列表', hint: '对列表里的每一项依次执行下面的步骤，用 item 引用当前项、index 引用序号。' },
};

/** 可以包含子步骤的步骤类型。 */
export const workflowControlKinds: WorkflowStepKind[] = ['branch', 'for_each'];

export const crmDealStatus: Record<CrmDealStatus, { label: string; tone: Tone }> = {
  open: { label: '进行中', tone: 'default' },
  won: { label: '赢单', tone: 'secondary' },
  lost: { label: '输单', tone: 'outline' },
};

export const crmStageKind: Record<CrmStageKind, string> = {
  open: '进行中',
  won: '赢单',
  lost: '输单',
};

export const crmActivityKind: Record<CrmActivityKind, string> = {
  note: '备注',
  call: '电话',
  meeting: '会议',
  email: '邮件',
  stage_change: '阶段变化',
};

export const crmRecordType: Record<CrmRecordType, string> = {
  company: '客户',
  contact: '联系人',
  deal: '商机',
};

export const crmFieldType: Record<CrmFieldType, { label: string; hint: string }> = {
  text: { label: '文本', hint: '一段文字，最多 2000 字' },
  number: { label: '数字', hint: '可以有小数和负数' },
  date: { label: '日期', hint: '年月日' },
  select: { label: '单选', hint: '从选项里选一个' },
  multi_select: { label: '多选', hint: '从选项里选多个' },
  checkbox: { label: '是否', hint: '勾选或不勾选' },
};

export const crmImportDuplicateMode: Record<CrmImportDuplicateMode, { label: string; hint: string }> = {
  skip: { label: '跳过', hint: '已有的记录保持不变' },
  update: { label: '更新', hint: '用表格里有值的列更新已有记录' },
  create: { label: '仍然新建', hint: '不检查重复，每行都新建' },
};

export const erpSalesOrderStatus: Record<ErpSalesOrderStatus, { label: string; tone: Tone }> = {
  draft: { label: '草稿', tone: 'outline' },
  confirmed: { label: '待发货', tone: 'default' },
  partially_shipped: { label: '部分发货', tone: 'default' },
  shipped: { label: '已发货', tone: 'secondary' },
  cancelled: { label: '已取消', tone: 'outline' },
};

export const erpPurchaseOrderStatus: Record<ErpPurchaseOrderStatus, { label: string; tone: Tone }> = {
  draft: { label: '草稿', tone: 'outline' },
  ordered: { label: '待收货', tone: 'default' },
  partially_received: { label: '部分到货', tone: 'default' },
  received: { label: '已入库', tone: 'secondary' },
  cancelled: { label: '已取消', tone: 'outline' },
};

export const erpMovementKind: Record<ErpStockMovementKind, string> = {
  purchase: '采购入库',
  sale: '销售出库',
  adjustment: '库存调整',
  transfer_in: '调拨入库',
  transfer_out: '调拨出库',
  sales_return: '客户退货',
  purchase_return: '退给供应商',
};

export const erpStockDocumentKind: Record<ErpStockDocumentKind, string> = {
  shipment: '发货单',
  receipt: '收货单',
  sales_return: '客户退货单',
  purchase_return: '采购退货单',
};

export const mcpApprovalMode: Record<McpApprovalMode, { label: string; hint: string }> = {
  writes_only: { label: '只读工具免审批', hint: '管理员确认为只读的工具直接执行，其余工具智能体调用前先提交审批。服务自己标注的只读只作参考。' },
  always: { label: '全部需审批', hint: '智能体调用这个服务的任何工具前都先提交审批。' },
  never: { label: '不需审批', hint: '智能体可以直接调用全部工具。只在完全信任这个服务时选择。' },
};

/** 数量最多 4 位小数，整数不显示小数部分。 */
export function formatQuantity(value: number | null | undefined, unit?: string) {
  if (value === null || value === undefined) {
    return '—';
  }

  const text = value.toLocaleString('zh-CN', { maximumFractionDigits: 4 });
  return unit ? `${text} ${unit}` : text;
}

/**
 * 金额按货币格式化；compact 用于看板等空间紧张的地方（例如 ¥12万）。
 * 不到一万时紧凑写法没有千位分隔（¥8000），与别处的 ¥8,000 不一致，所以照常显示。
 */
export function formatAmount(amount: number | null | undefined, currency = 'CNY', compact = false) {
  if (amount === null || amount === undefined) {
    return '—';
  }

  const shorten = compact && Math.abs(amount) >= 10_000;
  try {
    return new Intl.NumberFormat('zh-CN', {
      style: 'currency',
      currency,
      notation: shorten ? 'compact' : 'standard',
      minimumFractionDigits: 0,
      maximumFractionDigits: shorten ? 1 : compact ? 0 : 2,
    }).format(amount);
  } catch {
    return `${currency} ${amount.toLocaleString('zh-CN')}`;
  }
}

/** 多币种合计：每种货币一段，用「 + 」连接。 */
export function formatTotals(totals: { amount: number; currency: string }[] | undefined, compact = false) {
  return totals?.length ? totals.map((t) => formatAmount(t.amount, t.currency, compact)).join(' + ') : formatAmount(0, 'CNY', compact);
}

/** 只有日期的值（yyyy-MM-dd），按本地日期显示，不做时区换算。 */
export function formatDate(value?: string | null) {
  if (!value) {
    return '—';
  }

  const [year, month, day] = value.split('-').map(Number);
  return new Date(year, month - 1, day).toLocaleDateString('zh-CN');
}

/** 截止日期是否已过（按本地日期比较，当天不算逾期）。 */
export function isPastDate(value?: string | null) {
  if (!value) {
    return false;
  }

  const now = new Date();
  const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
  return value < today;
}

export function formatTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}

export function formatUsd(value: number) {
  return `$${value.toFixed(value < 1 ? 4 : 2)}`;
}

/** 本机时区（IANA 名称），注册空间时作为默认的统计时区。 */
export function localTimeZone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}
