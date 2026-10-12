'use client';

import Link from 'next/link';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { AnalyticsChart, AnalyticsInterval, AnalyticsKpiDto, AnalyticsRange, MetricResultDto } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Card, CardContent } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';
import { comparisonLabels, describeBucket, describeChange, formatMetric, rangeLabels, ranges } from './format';
import { ChartLegend, TrendChart, type ChartSeries } from './trend-chart';

export interface MetricRequest {
  metric: string;
  range?: AnalyticsRange;
  from?: string;
  to?: string;
  interval: AnalyticsInterval;
  breakdown?: string;
  currency?: string;
  compare?: boolean;
  limit?: number;
}

/** 查询一个指标；参数变化时保留上一次的结果，切换选项不闪烁。 */
export function useMetricQuery(request: MetricRequest | null) {
  return useQuery({
    queryKey: queryKeys.analyticsQuery(request ?? {}),
    queryFn: ({ signal }) => {
      const r = request!;
      return api.analytics.query(r.metric, r.from, r.to, r.range, r.interval, r.breakdown, r.currency, undefined, r.compare ?? true, r.limit, signal);
    },
    enabled: !!request,
    placeholderData: keepPreviousData,
    staleTime: 60_000,
  });
}

export function useMetricCatalog() {
  return useQuery({ queryKey: queryKeys.analyticsMetrics, queryFn: ({ signal }) => api.analytics.metrics(signal), staleTime: 5 * 60_000 });
}

/** 不拆分时画总量一条线；拆分时画各组。 */
export function chartSeries(result: MetricResultDto): (ChartSeries & { total?: number })[] {
  return result.series.length ? result.series : [{ key: 'total', label: result.name, points: result.points, total: result.total }];
}

export function MetricChart({ result, chart, height }: { result: MetricResultDto; chart: AnalyticsChart; height?: number }) {
  const series = chartSeries(result);
  return (
    <div className='space-y-3'>
      <TrendChart
        series={series}
        interval={result.interval}
        unit={result.unit}
        currency={result.currency}
        kind={chart}
        stacked={!result.isRatio}
        height={height}
      />
      <ChartLegend series={series.map((s) => ({ key: s.key, label: s.label, total: formatMetric(s.total, result.unit, result.currency) }))} />
    </div>
  );
}

export function ChangeBadge({ value, previous, isRatio, label }: { value?: number; previous?: number; isRatio: boolean; label?: string }) {
  const change = describeChange(value, previous, isRatio);
  if (!change) {
    return <span className='text-muted-foreground text-xs'>{label ? `${label}：无可比数据` : '无可比数据'}</span>;
  }

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 text-xs tabular-nums',
        change.direction === 'up' && 'text-emerald-600 dark:text-emerald-500',
        change.direction === 'down' && 'text-rose-600 dark:text-rose-500',
        change.direction === 'flat' && 'text-muted-foreground',
      )}
    >
      {change.direction === 'up' ? <Icons.trendingUp className='size-3.5' /> : change.direction === 'down' ? <Icons.trendingDown className='size-3.5' /> : null}
      {label ? `${label} ` : ''}
      {change.text}
    </span>
  );
}

/** 概览里的关键指标卡片，点开进入自由分析。 */
export function KpiCard({ kpi, range }: { kpi: AnalyticsKpiDto; range: AnalyticsRange }) {
  return (
    <Link href={`/analytics/explore?metric=${encodeURIComponent(kpi.metric)}&range=${range}`} className='group'>
      <Card className='group-hover:border-primary/40 h-full py-4 transition-colors'>
        <CardContent className='space-y-1 px-4'>
          <p className='text-muted-foreground text-xs'>{kpi.name}</p>
          <p className='truncate text-xl font-semibold tabular-nums'>{formatMetric(kpi.value, kpi.unit, kpi.currency)}</p>
          <ChangeBadge value={kpi.value} previous={kpi.previousValue} isRatio={kpi.isRatio} label={comparisonLabels[range]} />
        </CardContent>
      </Card>
    </Link>
  );
}

export function RangeSelect({ value, onChange, className }: { value: AnalyticsRange; onChange: (range: AnalyticsRange) => void; className?: string }) {
  return (
    <Select value={value} onValueChange={(v) => onChange(v as AnalyticsRange)}>
      <SelectTrigger className={cn('w-32', className)} aria-label='统计区间'>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {ranges.map((range) => (
          <SelectItem key={range} value={range}>
            {rangeLabels[range]}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}

/** 明细表：每个时间段一行，拆分时每组一列；最后一行是合计。 */
export function MetricTable({ result }: { result: MetricResultDto }) {
  const series = chartSeries(result);
  const showTotal = series.length > 1;
  return (
    <div className='overflow-x-auto rounded-md border'>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead className='min-w-32'>时间</TableHead>
            {series.map((s) => (
              <TableHead key={s.key} className='text-right'>
                {s.label}
              </TableHead>
            ))}
            {showTotal ? <TableHead className='text-right'>{result.isRatio ? '整体' : '合计'}</TableHead> : null}
          </TableRow>
        </TableHeader>
        <TableBody>
          {result.points.map((point, i) => (
            <TableRow key={point.date}>
              <TableCell className='text-muted-foreground'>{describeBucket(point.date, result.interval)}</TableCell>
              {series.map((s) => (
                <TableCell key={s.key} className='text-right tabular-nums'>
                  {formatMetric(s.points[i]?.value, result.unit, result.currency)}
                </TableCell>
              ))}
              {showTotal ? <TableCell className='text-right font-medium tabular-nums'>{formatMetric(point.value, result.unit, result.currency)}</TableCell> : null}
            </TableRow>
          ))}
        </TableBody>
        <TableFooter>
          <TableRow>
            <TableCell>{result.isRatio ? '整个区间' : '合计'}</TableCell>
            {series.map((s) => (
              <TableCell key={s.key} className='text-right tabular-nums'>
                {formatMetric(s.total, result.unit, result.currency)}
              </TableCell>
            ))}
            {showTotal ? <TableCell className='text-right tabular-nums'>{formatMetric(result.total, result.unit, result.currency)}</TableCell> : null}
          </TableRow>
        </TableFooter>
      </Table>
    </div>
  );
}

/** 导出 CSV（带 BOM，Excel 打开中文不乱码）。数值不做格式化，方便再计算。 */
export function downloadCsv(result: MetricResultDto) {
  const series = chartSeries(result);
  const escape = (text: string) => (/[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text);
  const unit = result.unit === 'amount' ? `（${result.currency ?? ''}）` : result.unit === 'percent' ? '（%）' : result.unit === 'usd' ? '（USD）' : '';
  const header = ['日期', ...series.map((s) => `${s.label}${unit}`), ...(series.length > 1 ? [`${result.isRatio ? '整体' : '合计'}${unit}`] : [])];
  const rows = result.points.map((point, i) => [
    point.date,
    ...series.map((s) => String(s.points[i]?.value ?? '')),
    ...(series.length > 1 ? [String(point.value ?? '')] : []),
  ]);
  const csv = [header, ...rows].map((row) => row.map(escape).join(',')).join('\r\n');
  const url = URL.createObjectURL(new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  // 文件名只用 ASCII（指标键 + 日期）：部分浏览器内核遇到中文文件名会退回成「download」。
  link.download = `${result.metric.replace(/\./g, '-')}_${result.from}_${result.to}.csv`;
  // 部分浏览器只认挂在文档里的链接的 download 属性；下载开始后再释放地址。
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1_000);
}
