import type {
  ApprovalStatus,
  CrmActivityKind,
  CrmDealStatus,
  ErpPurchaseOrderStatus,
  ErpSalesOrderStatus,
  ErpStockMovementKind,
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

export const crmDealStatus: Record<CrmDealStatus, string> = {
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

export const erpSalesOrderStatus: Record<ErpSalesOrderStatus, string> = {
  draft: '草稿',
  confirmed: '待发货',
  shipped: '已发货',
  cancelled: '已取消',
};

export const erpPurchaseOrderStatus: Record<ErpPurchaseOrderStatus, string> = {
  draft: '草稿',
  ordered: '待收货',
  received: '已入库',
  cancelled: '已取消',
};

export const erpMovementKind: Record<ErpStockMovementKind, string> = {
  purchase: '采购入库',
  sale: '销售出库',
  adjustment: '库存调整',
  transfer_in: '调拨入库',
  transfer_out: '调拨出库',
};

/** 数量最多 4 位小数，整数不显示小数部分。 */
export function formatQuantity(value: number | null | undefined, unit?: string) {
  if (value === null || value === undefined) {
    return '—';
  }

  const text = String(Math.round(value * 10_000) / 10_000);
  return unit ? `${text} ${unit}` : text;
}

const currencySymbols: Record<string, string> = { CNY: '¥', USD: '$', EUR: '€', HKD: 'HK$', JPY: '¥', GBP: '£' };

/** 金额加千分位和货币符号。不依赖 Intl 的货币格式，各平台的 JS 引擎表现一致。 */
export function formatAmount(amount: number | null | undefined, currency = 'CNY') {
  if (amount === null || amount === undefined) {
    return '—';
  }

  const [whole = '0', fraction] = (Math.round(amount * 100) / 100).toString().split('.');
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  const symbol = currencySymbols[currency] ?? `${currency} `;
  return `${symbol}${grouped}${fraction ? `.${fraction}` : ''}`;
}

/** 多币种合计：每种货币一段，用「 + 」连接。 */
export function formatTotals(totals: { amount: number; currency: string }[] | undefined) {
  return totals?.length ? totals.map((t) => formatAmount(t.amount, t.currency)).join(' + ') : formatAmount(0);
}

/** 只有日期的值（yyyy-MM-dd）原样按本地日期显示。 */
export function formatDate(value?: string | null) {
  if (!value) {
    return '—';
  }

  const [year, month, day] = value.split('-').map(Number);
  return `${year}/${month}/${day}`;
}

export function formatTime(value?: string | null) {
  if (!value) {
    return '—';
  }

  return new Date(value).toLocaleString('zh-CN', { hour12: false });
}
