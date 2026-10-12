import type { AnalyticsInterval, AnalyticsRange, MetricUnit } from '@ainative/api-client';
import { formatAmount } from '@/lib/labels';

export const rangeLabels: Record<AnalyticsRange, string> = {
  last7_days: '近 7 天',
  last30_days: '近 30 天',
  last90_days: '近 90 天',
  this_month: '本月',
  last_month: '上月',
  this_quarter: '本季度',
  this_year: '今年',
  last12_months: '近 12 个月',
};

export const ranges = Object.keys(rangeLabels) as AnalyticsRange[];

export const intervalLabels: Record<AnalyticsInterval, string> = { day: '按天', week: '按周', month: '按月' };

/** 环比对象的说法，和后端的对比区间一致。 */
export const comparisonLabels: Record<AnalyticsRange, string> = {
  last7_days: '较前 7 天',
  last30_days: '较前 30 天',
  last90_days: '较前 90 天',
  this_month: '较上月同期',
  last_month: '较前一个月',
  this_quarter: '较上季度同期',
  this_year: '较去年同期',
  last12_months: '较前 12 个月',
};

/** 快捷区间对应的默认粒度，趋势保持在几十个点以内。 */
export function defaultInterval(range: AnalyticsRange): AnalyticsInterval {
  switch (range) {
    case 'last90_days':
    case 'this_quarter':
      return 'week';
    case 'this_year':
    case 'last12_months':
      return 'month';
    default:
      return 'day';
  }
}

/** 按指标单位格式化数值；compact 用于坐标轴等空间紧张处。 */
export function formatMetric(value: number | null | undefined, unit: MetricUnit, currency?: string | null, compact = false) {
  if (value === null || value === undefined) {
    return '—';
  }

  switch (unit) {
    case 'amount':
      return formatAmount(value, currency ?? 'CNY', compact);
    case 'percent':
      return `${value.toLocaleString('zh-CN', { maximumFractionDigits: 1 })}%`;
    case 'usd':
      return compact || value >= 1 || value === 0 ? `$${value.toLocaleString('en-US', { maximumFractionDigits: 2 })}` : `$${value.toFixed(4)}`;
    case 'tokens':
      return new Intl.NumberFormat('zh-CN', { notation: 'compact', maximumFractionDigits: 1 }).format(value);
    default:
      return compact
        ? new Intl.NumberFormat('zh-CN', { notation: 'compact', maximumFractionDigits: 1 }).format(value)
        : value.toLocaleString('zh-CN', { maximumFractionDigits: 2 });
  }
}

/**
 * 与上期相比的变化。比率类指标给出百分点差，其他给出百分比；上期为 0 或没有数据时没有可比的变化。
 */
export function describeChange(value: number | null | undefined, previous: number | null | undefined, isRatio: boolean) {
  if (value === null || value === undefined || previous === null || previous === undefined) {
    return null;
  }

  if (value === previous) {
    return { direction: 'flat' as const, text: '持平' };
  }

  const delta = isRatio ? value - previous : previous === 0 ? null : ((value - previous) / previous) * 100;
  if (delta === null) {
    return value > 0 ? { direction: 'up' as const, text: '上期为 0' } : null;
  }

  const rounded = Math.round(delta * 10) / 10;
  const direction = rounded > 0 ? ('up' as const) : rounded < 0 ? ('down' as const) : ('flat' as const);
  const sign = rounded > 0 ? '+' : '';
  return { direction, text: isRatio ? `${sign}${rounded} 个百分点` : `${sign}${rounded}%` };
}

/** 趋势图横轴上的日期（yyyy-MM-dd → 短格式）。 */
export function formatBucket(date: string, interval: AnalyticsInterval, withYear = false) {
  const [year, month, day] = date.split('-').map(Number);
  if (interval === 'month') {
    return withYear ? `${year}年${month}月` : `${month}月`;
  }

  return withYear ? `${year}/${month}/${day}` : `${month}/${day}`;
}

/** 提示框里的完整日期：周显示"起"，月显示年月。 */
export function describeBucket(date: string, interval: AnalyticsInterval) {
  const [year, month, day] = date.split('-').map(Number);
  if (interval === 'month') {
    return `${year}年${month}月`;
  }

  return interval === 'week' ? `${year}年${month}月${day}日起的一周` : `${year}年${month}月${day}日`;
}

/** 指标分组显示用的模块名称。 */
export const moduleLabels: Record<string, string> = {
  crm: '客户',
  erp: '进销存',
  agents: '任务与智能体',
  platform: 'AI 用量',
  workflows: '自动化',
};
