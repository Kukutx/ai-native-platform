'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import type { AnalyticsOverviewDto, AnalyticsRange, MetricDefinitionDto } from '@ainative/api-client';
import { PageHeader } from '@/components/app/page-header';
import { StatusBadge } from '@/components/app/status-badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { AnalyticsNav } from '@/features/analytics/analytics-nav';
import { defaultInterval, formatMetric, rangeLabels } from '@/features/analytics/format';
import { KpiCard, MetricChart, RangeSelect, useMetricCatalog, useMetricQuery } from '@/features/analytics/metric-view';
import { api } from '@/lib/api';
import { formatDate, formatQuantity, formatTotals, formatUsd, projectStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

/** 概览里的趋势图，按可用性依次取前四个。 */
const TREND_METRICS = ['crm.deals_won', 'erp.sales_shipped', 'tasks.completed', 'ai.cost', 'agents.runs', 'workflows.runs'];

export default function AnalyticsOverviewPage() {
  const [range, setRange] = useState<AnalyticsRange>('this_month');
  const overview = useQuery({
    queryKey: queryKeys.analyticsOverview(range),
    queryFn: ({ signal }) => api.analytics.overview(undefined, undefined, range, undefined, signal),
    staleTime: 60_000,
  });
  const catalog = useMetricCatalog();
  const trends = TREND_METRICS.map((key) => catalog.data?.find((m) => m.key === key))
    .filter((m): m is MetricDefinitionDto => !!m)
    .slice(0, 4);

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='经营分析'
        description={
          overview.data
            ? `${rangeLabels[range]}（${formatDate(overview.data.from)} – ${formatDate(overview.data.to)}），按空间时区 ${overview.data.timeZone} 统计。`
            : '销售、库存、任务和智能体的关键数字，点任意指标进入自由分析。'
        }
        actions={<RangeSelect value={range} onChange={setRange} />}
      />
      <AnalyticsNav />
      {overview.isPending ? (
        <div className='grid grid-cols-2 gap-3 sm:gap-4 lg:grid-cols-4'>
          {Array.from({ length: 8 }, (_, i) => (
            <Skeleton key={i} className='h-24' />
          ))}
        </div>
      ) : overview.data ? (
        <>
          <div className='grid grid-cols-2 gap-3 sm:gap-4 lg:grid-cols-4'>
            {overview.data.kpis.map((kpi) => (
              <KpiCard key={kpi.metric} kpi={kpi} range={range} />
            ))}
          </div>
          {trends.length ? (
            <div className='grid gap-4 lg:grid-cols-2'>
              {trends.map((metric) => (
                <TrendCard key={metric.key} metric={metric} range={range} />
              ))}
            </div>
          ) : null}
          <Sections overview={overview.data} />
        </>
      ) : null}
    </div>
  );
}

function TrendCard({ metric, range }: { metric: MetricDefinitionDto; range: AnalyticsRange }) {
  const query = useMetricQuery({ metric: metric.key, range, interval: defaultInterval(range), compare: false });
  return (
    <Card>
      <CardHeader className='flex flex-row items-start justify-between gap-2'>
        <div className='space-y-1'>
          <CardTitle className='text-sm font-medium'>
            <Link href={`/analytics/explore?metric=${encodeURIComponent(metric.key)}&range=${range}`} className='hover:underline'>
              {metric.name}
            </Link>
          </CardTitle>
          <p className='text-2xl font-semibold tabular-nums'>{query.data ? formatMetric(query.data.total, query.data.unit, query.data.currency) : '—'}</p>
        </div>
        {query.data && query.data.currencies.length > 1 ? (
          <span className='text-muted-foreground text-xs'>另有 {query.data.currencies.filter((c) => c !== query.data?.currency).join('、')}</span>
        ) : null}
      </CardHeader>
      <CardContent>{query.data ? <MetricChart result={query.data} chart={metric.unit === 'count' ? 'bar' : 'line'} height={180} /> : <Skeleton className='h-44' />}</CardContent>
    </Card>
  );
}

function Sections({ overview }: { overview: AnalyticsOverviewDto }) {
  return (
    <div className='grid gap-4 xl:grid-cols-2'>
      {overview.pipeline ? (
        <Card>
          <CardHeader>
            <CardTitle className='text-sm font-medium'>销售漏斗（进行中的商机）</CardTitle>
          </CardHeader>
          <CardContent className='space-y-3'>
            {overview.pipeline.map((stage) => {
              const max = Math.max(...(overview.pipeline ?? []).map((s) => s.deals), 1);
              return (
                <div key={stage.stageId} className='space-y-1'>
                  <div className='flex items-baseline justify-between gap-2 text-sm'>
                    <span>
                      {stage.name}
                      <span className='text-muted-foreground ml-2 text-xs'>{stage.probability}%</span>
                    </span>
                    <span className='text-muted-foreground text-xs tabular-nums'>
                      {stage.deals} 个 · {formatTotals(stage.amounts, true)}
                    </span>
                  </div>
                  <div className='bg-muted h-2 overflow-hidden rounded-full'>
                    <div className='bg-primary h-full rounded-full' style={{ width: `${(stage.deals / max) * 100}%` }} />
                  </div>
                </div>
              );
            })}
          </CardContent>
        </Card>
      ) : null}
      {overview.projects ? (
        <Card>
          <CardHeader>
            <CardTitle className='text-sm font-medium'>项目进度</CardTitle>
          </CardHeader>
          <CardContent>
            {overview.projects.length ? (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>项目</TableHead>
                    <TableHead className='w-32'>进度</TableHead>
                    <TableHead className='text-right'>未完成</TableHead>
                    <TableHead className='text-right'>目标日期</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {overview.projects.map((project) => (
                    <TableRow key={project.projectId}>
                      <TableCell className='max-w-48'>
                        <Link href={`/projects/view?id=${project.projectId}`} className='block truncate hover:underline'>
                          {project.name}
                        </Link>
                        <StatusBadge meta={projectStatus[project.status]} />
                      </TableCell>
                      <TableCell>
                        <div className='bg-muted h-1.5 overflow-hidden rounded-full'>
                          <div className='bg-primary h-full rounded-full' style={{ width: `${project.percent}%` }} />
                        </div>
                        <span className='text-muted-foreground text-xs tabular-nums'>{project.percent}%</span>
                      </TableCell>
                      <TableCell className='text-right text-xs tabular-nums'>
                        {project.openTasks}
                        {project.blocked ? <span className='text-destructive block'>受阻 {project.blocked}</span> : null}
                        {project.overdue ? <span className='block text-amber-600 dark:text-amber-500'>逾期 {project.overdue}</span> : null}
                      </TableCell>
                      <TableCell className={cn('text-right text-xs', project.isOverdue && 'text-destructive')}>{formatDate(project.targetDate)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <p className='text-muted-foreground text-sm'>没有进行中的项目。</p>
            )}
          </CardContent>
        </Card>
      ) : null}
      {overview.agents ? (
        <Card>
          <CardHeader>
            <CardTitle className='text-sm font-medium'>智能体工作量</CardTitle>
          </CardHeader>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>智能体</TableHead>
                  <TableHead className='text-right'>待办</TableHead>
                  <TableHead className='text-right'>执行</TableHead>
                  <TableHead className='text-right'>失败</TableHead>
                  <TableHead className='text-right'>花费</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {overview.agents.map((agent) => (
                  <TableRow key={agent.agentId}>
                    <TableCell className='max-w-48'>
                      <Link href={`/agents/view?id=${agent.agentId}`} className='block truncate hover:underline'>
                        {agent.name}
                      </Link>
                      <span className='text-muted-foreground block truncate text-xs'>{agent.title}</span>
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{agent.openTasks}</TableCell>
                    <TableCell className='text-right tabular-nums'>{agent.runs}</TableCell>
                    <TableCell className={cn('text-right tabular-nums', agent.failedRuns > 0 && 'text-destructive')}>{agent.failedRuns}</TableCell>
                    <TableCell className='text-right tabular-nums'>{formatUsd(agent.costUsd)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      ) : null}
      {overview.lowStock ? (
        <Card>
          <CardHeader>
            <CardTitle className='text-sm font-medium'>库存预警</CardTitle>
          </CardHeader>
          <CardContent>
            {overview.lowStock.length ? (
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>产品</TableHead>
                    <TableHead className='text-right'>可用</TableHead>
                    <TableHead className='text-right'>补货点</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {overview.lowStock.map((item) => (
                    <TableRow key={item.productId}>
                      <TableCell className='max-w-56'>
                        <Link href={`/erp/products/view?id=${item.productId}`} className='block truncate hover:underline'>
                          <span className='text-muted-foreground mr-2 font-mono text-xs'>{item.sku}</span>
                          {item.name}
                        </Link>
                      </TableCell>
                      <TableCell className='text-right text-amber-600 tabular-nums dark:text-amber-500'>{formatQuantity(item.available, item.unit)}</TableCell>
                      <TableCell className='text-muted-foreground text-right tabular-nums'>{formatQuantity(item.reorderPoint, item.unit)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ) : (
              <p className='text-muted-foreground text-sm'>所有产品库存都在补货点以上。</p>
            )}
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
