'use client';

import { Suspense, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import type { AnalyticsChart, AnalyticsInterval, AnalyticsRange, AnalyticsReportDto, MetricDefinitionDto } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { DetailState, ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { AnalyticsNav } from '@/features/analytics/analytics-nav';
import { comparisonLabels, defaultInterval, formatMetric, intervalLabels, moduleLabels, rangeLabels, ranges } from '@/features/analytics/format';
import { ChangeBadge, MetricChart, MetricTable, downloadCsv, useMetricCatalog, useMetricQuery, type MetricRequest } from '@/features/analytics/metric-view';
import { ReportDialog } from '@/features/analytics/report-dialog';
import { api } from '@/lib/api';
import { formatDate } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

const CUSTOM = 'custom';
const NONE = 'none';
const AUTO = 'auto';

export default function AnalyticsExplorePage() {
  return (
    <Suspense>
      <ExploreLoader />
    </Suspense>
  );
}

interface ExploreState {
  metric: string;
  breakdown: string;
  range: AnalyticsRange | typeof CUSTOM;
  from: string;
  to: string;
  interval: AnalyticsInterval;
  chart: AnalyticsChart;
  currency: string;
}

function ExploreLoader() {
  const params = useSearchParams();
  const reportId = params.get('report') ?? '';
  const catalog = useMetricCatalog();
  const report = useQuery({
    queryKey: queryKeys.analyticsReport(reportId),
    queryFn: ({ signal }) => api.analytics.getReport(reportId, signal),
    enabled: !!reportId,
  });
  // 「载入最新」时递增，用缓存里的最新报表重新开始编辑；别人修改报表时不自动打断正在进行的调整。
  const [revision, setRevision] = useState(0);

  if (reportId && report.isError) {
    return <DetailState id={reportId} query={report} subject='报表' back={{ href: '/analytics/reports', label: '报表' }} />;
  }

  if (catalog.isError) {
    return (
      <div className='space-y-6 p-6'>
        <PageHeader title='自由分析' />
        <AnalyticsNav />
        <ErrorState query={catalog} />
      </div>
    );
  }

  if (catalog.isPending || (reportId && report.isPending)) {
    return (
      <div className='space-y-6 p-6'>
        <Skeleton className='h-10 w-64' />
        <Skeleton className='h-96' />
      </div>
    );
  }

  const metrics = catalog.data ?? [];
  if (!metrics.length) {
    return (
      <div className='space-y-6 p-6'>
        <PageHeader title='自由分析' />
        <AnalyticsNav />
        <EmptyState title='没有可查看的指标' description='相关模块没有启用，或你的角色没有对应的查看权限。' />
      </div>
    );
  }

  const saved = report.data ?? null;
  const range = (params.get('range') as AnalyticsRange | null) ?? 'last30_days';
  const initial: ExploreState = saved
    ? {
        metric: saved.metric,
        breakdown: saved.breakdown ?? NONE,
        range: saved.range,
        from: '',
        to: '',
        interval: saved.interval,
        chart: saved.chart,
        currency: saved.currency ?? AUTO,
      }
    : {
        metric: metrics.some((m) => m.key === params.get('metric')) ? params.get('metric')! : metrics[0].key,
        breakdown: NONE,
        range: ranges.includes(range) ? range : 'last30_days',
        from: '',
        to: '',
        interval: defaultInterval(ranges.includes(range) ? range : 'last30_days'),
        chart: 'line',
        currency: AUTO,
      };

  return (
    <Explorer
      key={saved ? `${saved.id}:${revision}` : 'new'}
      metrics={metrics}
      report={saved}
      initial={initial}
      onReload={() => setRevision((r) => r + 1)}
    />
  );
}

function Explorer({
  metrics,
  report,
  initial,
  onReload,
}: {
  metrics: MetricDefinitionDto[];
  report: AnalyticsReportDto | null;
  initial: ExploreState;
  onReload: () => void;
}) {
  const router = useRouter();
  const { can } = useSession();
  const [state, setState] = useState(initial);
  // 开始编辑时的报表版本：保存时提交它，别人在这期间改过报表会得到冲突提示，而不是被悄悄覆盖。
  const [baseVersion, setBaseVersion] = useState(report?.updatedAt);
  const outdated = !!report && !!baseVersion && report.updatedAt !== baseVersion;
  const [saving, setSaving] = useState<'update' | 'create' | null>(null);
  const metric = metrics.find((m) => m.key === state.metric) ?? metrics[0];
  const custom = state.range === CUSTOM;
  const request: MetricRequest | null =
    custom && (!state.from || !state.to)
      ? null
      : {
          metric: metric.key,
          range: custom ? undefined : (state.range as AnalyticsRange),
          from: custom ? state.from : undefined,
          to: custom ? state.to : undefined,
          interval: state.interval,
          breakdown: state.breakdown === NONE ? undefined : state.breakdown,
          currency: state.currency === AUTO ? undefined : state.currency,
          limit: 8,
        };
  const query = useMetricQuery(request);
  const result = query.data;
  const update = (patch: Partial<ExploreState>) => setState((s) => ({ ...s, ...patch }));
  const groups = [...new Set(metrics.map((m) => m.moduleKey))].map((moduleKey) => [moduleKey, metrics.filter((m) => m.moduleKey === moduleKey)] as const);
  const manage = can('analytics.manage');

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title={report ? report.name : '自由分析'}
        description={report?.description || '选一个指标，按时间看趋势，按负责人、客户、产品等维度拆开比较，可以导出或保存成报表。'}
        actions={
          <>
            <Button variant='outline' disabled={!result} onClick={() => result && downloadCsv(result)}>
              <Icons.download />
              导出 CSV
            </Button>
            {manage ? (
              report ? (
                <>
                  <Button variant='outline' disabled={custom} onClick={() => setSaving('create')}>
                    另存为
                  </Button>
                  <Button disabled={custom} onClick={() => setSaving('update')}>
                    保存
                  </Button>
                </>
              ) : (
                <Button disabled={custom} onClick={() => setSaving('create')} title={custom ? '报表按快捷区间滚动计算，自定义区间不能保存' : undefined}>
                  保存为报表
                </Button>
              )
            ) : null}
          </>
        }
      />
      <AnalyticsNav />
      {outdated ? (
        <Alert>
          <Icons.info />
          <AlertTitle>这张报表刚被其他人修改过</AlertTitle>
          <AlertDescription className='flex flex-wrap items-center gap-x-4 gap-y-2'>
            <span>当前显示的是你打开时的设置，载入最新会放弃你在这里的调整。</span>
            <Button size='sm' variant='outline' onClick={onReload}>
              <Icons.refresh />
              载入最新
            </Button>
          </AlertDescription>
        </Alert>
      ) : null}
      <Card>
        <CardContent className='grid gap-4 sm:grid-cols-2 lg:grid-cols-4'>
          <div className='grid gap-2'>
            <Label>指标</Label>
            <Select
              value={metric.key}
              onValueChange={(key) => {
                const next = metrics.find((m) => m.key === key);
                update({
                  metric: key,
                  currency: AUTO,
                  breakdown: next?.dimensions.some((d) => d.key === state.breakdown) ? state.breakdown : NONE,
                });
              }}
            >
              <SelectTrigger className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {groups.map(([moduleKey, items]) => (
                  <SelectGroup key={moduleKey}>
                    <SelectLabel>{moduleLabels[moduleKey] ?? moduleKey}</SelectLabel>
                    {items.map((m) => (
                      <SelectItem key={m.key} value={m.key}>
                        {m.name}
                      </SelectItem>
                    ))}
                  </SelectGroup>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className='grid gap-2'>
            <Label>拆分</Label>
            <Select value={state.breakdown} onValueChange={(breakdown) => update({ breakdown })} disabled={!metric.dimensions.length}>
              <SelectTrigger className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>不拆分</SelectItem>
                {metric.dimensions.map((d) => (
                  <SelectItem key={d.key} value={d.key}>
                    按{d.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className='grid gap-2'>
            <Label>区间</Label>
            <Select
              value={state.range}
              onValueChange={(value) =>
                value === CUSTOM
                  ? update({ range: CUSTOM, from: result?.from ?? '', to: result?.to ?? '' })
                  : update({ range: value as AnalyticsRange, interval: defaultInterval(value as AnalyticsRange) })
              }
            >
              <SelectTrigger className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {ranges.map((r) => (
                  <SelectItem key={r} value={r}>
                    {rangeLabels[r]}
                  </SelectItem>
                ))}
                <SelectItem value={CUSTOM}>自定义…</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className='grid gap-2'>
            <Label>粒度与图表</Label>
            <div className='flex gap-2'>
              <Select value={state.interval} onValueChange={(interval) => update({ interval: interval as AnalyticsInterval })}>
                <SelectTrigger className='flex-1'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(Object.keys(intervalLabels) as AnalyticsInterval[]).map((i) => (
                    <SelectItem key={i} value={i}>
                      {intervalLabels[i]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <div className='flex rounded-md border p-0.5'>
                {(['line', 'bar'] as const).map((chart) => (
                  <Button
                    key={chart}
                    type='button'
                    size='icon'
                    variant='ghost'
                    className={cn('size-8', state.chart === chart && 'bg-muted')}
                    aria-label={chart === 'line' ? '折线图' : '柱状图'}
                    aria-pressed={state.chart === chart}
                    onClick={() => update({ chart })}
                  >
                    {chart === 'line' ? <Icons.chartLine /> : <Icons.chartBar />}
                  </Button>
                ))}
              </div>
            </div>
          </div>
          {custom ? (
            <div className='grid grid-cols-2 gap-2 sm:col-span-2'>
              <div className='grid gap-2'>
                <Label htmlFor='explore-from'>开始日期</Label>
                <Input id='explore-from' type='date' value={state.from} max={state.to || undefined} onChange={(e) => update({ from: e.target.value })} />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor='explore-to'>结束日期</Label>
                <Input id='explore-to' type='date' value={state.to} min={state.from || undefined} onChange={(e) => update({ to: e.target.value })} />
              </div>
            </div>
          ) : null}
          {result && (result.currencies.length > 1 || state.currency !== AUTO) ? (
            <div className='grid gap-2'>
              <Label>货币</Label>
              <Select value={state.currency} onValueChange={(currency) => update({ currency })}>
                <SelectTrigger className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={AUTO}>自动（金额最多的）</SelectItem>
                  {result.currencies.map((c) => (
                    <SelectItem key={c} value={c}>
                      {c}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          ) : null}
          <p className='text-muted-foreground text-xs sm:col-span-2 lg:col-span-4'>{metric.description}</p>
        </CardContent>
      </Card>

      {!request ? (
        <EmptyState title='请选择开始和结束日期' />
      ) : query.isError && !result ? (
        <ErrorState query={query} title='查询失败' />
      ) : !result ? (
        <Skeleton className='h-96' />
      ) : (
        <>
          <Card className={cn(query.isFetching && 'opacity-70 transition-opacity')}>
            <CardHeader className='flex flex-row flex-wrap items-end justify-between gap-2'>
              <div className='space-y-1'>
                <p className='text-muted-foreground text-sm'>
                  {result.name} · {formatDate(result.from)} – {formatDate(result.to)}
                  {result.currency ? ` · ${result.currency}` : ''}
                </p>
                <p className='text-3xl font-semibold tabular-nums'>{formatMetric(result.total, result.unit, result.currency)}</p>
              </div>
              <ChangeBadge
                value={result.total}
                previous={result.previousTotal}
                isRatio={result.isRatio}
                label={custom ? '较前一段' : comparisonLabels[state.range as AnalyticsRange]}
              />
            </CardHeader>
            <CardContent>
              <MetricChart result={result} chart={state.chart} height={300} />
            </CardContent>
          </Card>
          <MetricTable result={result} />
          <p className='text-muted-foreground text-xs'>按空间时区 {result.timeZone} 统计；金额不同货币分开统计，不做汇率换算。</p>
        </>
      )}

      <ReportDialog
        open={saving !== null}
        report={saving === 'update' ? report : null}
        baseVersion={baseVersion}
        defaultName={report ? `${report.name}（副本）` : `${metric.name}${state.breakdown === NONE ? '' : `（按${metric.dimensions.find((d) => d.key === state.breakdown)?.name ?? ''}）`}`}
        config={{
          metric: metric.key,
          breakdown: state.breakdown === NONE ? undefined : state.breakdown,
          currency: state.currency === AUTO ? undefined : state.currency,
          range: custom ? 'last30_days' : (state.range as AnalyticsRange),
          interval: state.interval,
          chart: state.chart,
        }}
        onOpenChange={(open) => !open && setSaving(null)}
        onSaved={(saved) => {
          setBaseVersion(saved.updatedAt);
          router.replace(`/analytics/explore?report=${saved.id}`);
        }}
        onLoadLatest={onReload}
      />
    </div>
  );
}
