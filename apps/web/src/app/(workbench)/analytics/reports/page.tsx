'use client';

import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { AnalyticsReportDto, MetricDefinitionDto } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { AnalyticsNav } from '@/features/analytics/analytics-nav';
import { comparisonLabels, formatMetric, rangeLabels } from '@/features/analytics/format';
import { ChangeBadge, MetricChart, useMetricCatalog, useMetricQuery } from '@/features/analytics/metric-view';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

export default function AnalyticsReportsPage() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const reports = useQuery({ queryKey: queryKeys.analyticsReports, queryFn: ({ signal }) => api.analytics.reports(signal) });
  const catalog = useMetricCatalog();
  const manage = can('analytics.manage');
  const reorder = useMutation({
    mutationFn: (ids: string[]) => api.analytics.reorderReports({ ids }),
    onMutate: (ids) => {
      // 先在本地换好位置，避免卡片跳动。
      const current = queryClient.getQueryData<AnalyticsReportDto[]>(queryKeys.analyticsReports);
      if (current) {
        queryClient.setQueryData(
          queryKeys.analyticsReports,
          ids.map((id) => current.find((r) => r.id === id)).filter((r): r is AnalyticsReportDto => !!r),
        );
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.analyticsReports }),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.analytics.deleteReport(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.analyticsReports }),
  });

  const move = (index: number, offset: number) => {
    const ids = reports.data?.map((r) => r.id) ?? [];
    const target = index + offset;
    if (target < 0 || target >= ids.length) {
      return;
    }

    [ids[index], ids[target]] = [ids[target], ids[index]];
    reorder.mutate(ids);
  };

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='报表'
        description='保存下来的分析视图，每次打开都按最新数据和当前日期计算。'
        actions={
          manage ? (
            <Button asChild>
              <Link href='/analytics/explore'>
                <Icons.add />
                新建报表
              </Link>
            </Button>
          ) : null
        }
      />
      <AnalyticsNav />
      {reports.isPending || catalog.isPending ? (
        <div className='grid gap-4 lg:grid-cols-2'>
          <Skeleton className='h-72' />
          <Skeleton className='h-72' />
        </div>
      ) : reports.data?.length ? (
        <div className='grid gap-4 lg:grid-cols-2'>
          {reports.data.map((report, index) => (
            <ReportCard
              key={report.id}
              report={report}
              metric={catalog.data?.find((m) => m.key === report.metric)}
              manage={manage}
              first={index === 0}
              last={index === reports.data.length - 1}
              onMove={(offset) => move(index, offset)}
              onDelete={() => confirm(`删除报表「${report.name}」？`) && remove.mutate(report.id)}
            />
          ))}
        </div>
      ) : (
        <EmptyState title='还没有报表' description='在自由分析里调好指标、拆分和区间后保存，常看的数字就都在这里了。也可以让智能体帮你查数。'>
          {manage ? (
            <Button asChild>
              <Link href='/analytics/explore'>去自由分析</Link>
            </Button>
          ) : null}
        </EmptyState>
      )}
    </div>
  );
}

function ReportCard({
  report,
  metric,
  manage,
  first,
  last,
  onMove,
  onDelete,
}: {
  report: AnalyticsReportDto;
  metric?: MetricDefinitionDto;
  manage: boolean;
  first: boolean;
  last: boolean;
  onMove: (offset: number) => void;
  onDelete: () => void;
}) {
  // 指标不可见（模块停用或没有权限）时不发查询，直接说明原因。
  const query = useMetricQuery(
    metric
      ? { metric: report.metric, range: report.range, interval: report.interval, breakdown: report.breakdown, currency: report.currency, limit: 6 }
      : null,
  );

  return (
    <Card>
      <CardHeader className='flex flex-row items-start justify-between gap-2'>
        <div className='min-w-0 space-y-1'>
          <CardTitle className='truncate text-base'>
            <Link href={`/analytics/explore?report=${report.id}`} className='hover:underline'>
              {report.name}
            </Link>
          </CardTitle>
          <p className='text-muted-foreground truncate text-xs'>
            {rangeLabels[report.range]}
            {metric && report.breakdown ? ` · 按${metric.dimensions.find((d) => d.key === report.breakdown)?.name ?? report.breakdown}` : ''}
            {report.description ? ` · ${report.description}` : ''}
          </p>
        </div>
        {manage ? (
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant='ghost' size='icon' className='size-8 shrink-0' aria-label='报表操作'>
                <Icons.ellipsis />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align='end'>
              <DropdownMenuItem asChild>
                <Link href={`/analytics/explore?report=${report.id}`}>
                  <Icons.edit />
                  编辑
                </Link>
              </DropdownMenuItem>
              <DropdownMenuItem disabled={first} onSelect={() => onMove(-1)}>
                <Icons.arrowUp />
                前移
              </DropdownMenuItem>
              <DropdownMenuItem disabled={last} onSelect={() => onMove(1)}>
                <Icons.arrowDown />
                后移
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem variant='destructive' onSelect={onDelete}>
                <Icons.trash />
                删除
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        ) : null}
      </CardHeader>
      <CardContent className='space-y-3'>
        {!metric ? (
          <p className='text-muted-foreground text-sm'>这张报表用到的指标对你不可见：所属模块没有启用，或你的角色没有查看权限。</p>
        ) : query.data ? (
          <>
            <div className='flex flex-wrap items-baseline gap-x-3 gap-y-1'>
              <span className='text-2xl font-semibold tabular-nums'>{formatMetric(query.data.total, query.data.unit, query.data.currency)}</span>
              <ChangeBadge value={query.data.total} previous={query.data.previousTotal} isRatio={query.data.isRatio} label={comparisonLabels[report.range]} />
            </div>
            <MetricChart result={query.data} chart={report.chart} height={200} />
          </>
        ) : query.isError ? (
          <p className='text-muted-foreground text-sm'>加载失败。</p>
        ) : (
          <Skeleton className='h-56' />
        )}
      </CardContent>
    </Card>
  );
}
