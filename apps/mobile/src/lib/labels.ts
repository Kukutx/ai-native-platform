import type {
  AgentStatus,
  AnalyticsRange,
  ApprovalStatus,
  CrmActivityKind,
  CrmDealStatus,
  ErpPurchaseOrderStatus,
  ErpSalesOrderStatus,
  ErpStockMovementKind,
  MetricUnit,
  ProjectProgressDto,
  ProjectStatus,
  RunEventType,
  RunStatus,
  WakeupSource,
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

export const projectStatus: Record<ProjectStatus, string> = {
  planned: '计划中',
  active: '进行中',
  on_hold: '暂停',
  completed: '已完成',
  cancelled: '已取消',
};

/** 进度一句话：「3/5 已完成 · 60%」，有受阻或逾期时附在后面。 */
export function formatProgress(progress: ProjectProgressDto) {
  if (!progress.total) {
    return '还没有任务';
  }

  return [
    `${progress.done}/${progress.total} 已完成 · ${progress.percent}%`,
    progress.blocked ? `受阻 ${progress.blocked}` : '',
    progress.overdue ? `逾期 ${progress.overdue}` : '',
  ]
    .filter(Boolean)
    .join(' · ');
}

export const priority: Record<WorkItemPriority, string> = {
  low: '低',
  medium: '中',
  high: '高',
  urgent: '紧急',
};

export type Tone = 'muted' | 'text' | 'success' | 'warning' | 'danger';

export const agentStatus: Record<AgentStatus, { label: string; tone: Tone }> = {
  idle: { label: '空闲', tone: 'muted' },
  running: { label: '工作中', tone: 'success' },
  paused: { label: '已暂停', tone: 'warning' },
  error: { label: '出错', tone: 'danger' },
  terminated: { label: '已终止', tone: 'muted' },
};

export const runStatus: Record<RunStatus, { label: string; tone: Tone }> = {
  queued: { label: '排队中', tone: 'muted' },
  running: { label: '执行中', tone: 'warning' },
  succeeded: { label: '成功', tone: 'success' },
  failed: { label: '失败', tone: 'danger' },
  cancelled: { label: '已取消', tone: 'muted' },
  timed_out: { label: '超时', tone: 'danger' },
};

const finishedRunStatuses = new Set<string>(['succeeded', 'failed', 'cancelled', 'timed_out'] satisfies RunStatus[]);

/** 运行已经结束，不会再有新的过程事件（也接受实时事件里未经校验的状态字符串）。 */
export function runFinished(status: string) {
  return finishedRunStatuses.has(status);
}

export const runEventType: Record<RunEventType, string> = {
  status: '状态',
  assistant_text: '回复',
  tool_call: '调用动作',
  tool_result: '动作结果',
  approval_requested: '等待审批',
  error: '错误',
};

export const wakeupSource: Record<WakeupSource, string> = {
  on_demand: '手动唤醒',
  assignment: '任务指派',
  comment: '新评论',
  approval: '审批结果',
  timer: '定时心跳',
  delegation: '同事委派',
  automation: '自动化',
  dependency: '前置任务完成',
};

/** 美元金额（模型花费）：不足 1 美元时保留 4 位小数。 */
export function formatUsd(value: number) {
  return `$${value >= 1 || value === 0 ? value.toFixed(2) : value.toFixed(4)}`;
}

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
  webhook: 'Webhook',
};

export const workflowStepKind: Record<WorkflowStepKind, string> = {
  action: '执行动作',
  agent_task: '派给智能体',
  llm: '模型处理',
  delay: '等待',
  http: 'HTTP 请求',
  branch: '条件分支',
  for_each: '遍历列表',
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
  partially_shipped: '部分发货',
  shipped: '已发货',
  cancelled: '已取消',
};

export const erpPurchaseOrderStatus: Record<ErpPurchaseOrderStatus, string> = {
  draft: '草稿',
  ordered: '待收货',
  partially_received: '部分到货',
  received: '已入库',
  cancelled: '已取消',
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

export const analyticsRanges: { value: AnalyticsRange; label: string; comparison: string }[] = [
  { value: 'this_month', label: '本月', comparison: '较上月同期' },
  { value: 'last_month', label: '上月', comparison: '较前一个月' },
  { value: 'last30_days', label: '近 30 天', comparison: '较前 30 天' },
  { value: 'this_quarter', label: '本季度', comparison: '较上季度同期' },
  { value: 'this_year', label: '今年', comparison: '较去年同期' },
];

function compactNumber(value: number) {
  const abs = Math.abs(value);
  if (abs >= 100_000_000) {
    return `${Math.round((value / 100_000_000) * 10) / 10}亿`;
  }

  if (abs >= 10_000) {
    return `${Math.round((value / 10_000) * 10) / 10}万`;
  }

  return String(Math.round(value * 100) / 100);
}

/** 按指标单位显示数值（与 Web 端口径一致，不依赖 Intl）。 */
export function formatMetric(value: number | null | undefined, unit: MetricUnit, currency?: string | null) {
  if (value === null || value === undefined) {
    return '—';
  }

  switch (unit) {
    case 'amount':
      return formatAmount(value, currency ?? 'CNY');
    case 'percent':
      return `${Math.round(value * 10) / 10}%`;
    case 'usd':
      return formatUsd(value);
    case 'tokens':
      return compactNumber(value);
    default:
      return compactNumber(value);
  }
}

/** 与上期相比：比率类给百分点差，其他给百分比；上期为 0 时没有可比的变化。 */
export function describeChange(value: number | null | undefined, previous: number | null | undefined, isRatio: boolean): { up: boolean | null; text: string } | null {
  if (value === null || value === undefined || previous === null || previous === undefined) {
    return null;
  }

  if (value === previous) {
    return { up: null, text: '持平' };
  }

  if (!isRatio && previous === 0) {
    return value > 0 ? { up: true, text: '上期为 0' } : null;
  }

  const delta = Math.round((isRatio ? value - previous : ((value - previous) / previous) * 100) * 10) / 10;
  return { up: delta >= 0, text: `${delta > 0 ? '+' : ''}${delta}${isRatio ? ' 个百分点' : '%'}` };
}

/** 本机时区（IANA 名称），注册时作为空间的默认时区。 */
export function localTimeZone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}
