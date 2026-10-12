import { useState } from 'react';
import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { AnalyticsKpiDto, AnalyticsRange, MetricResultDto } from '@ainative/api-client';
import { ProgressBar } from '@/components/progress';
import { Card, Chip, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { analyticsRanges, describeChange, formatDate, formatMetric, formatQuantity, formatTotals } from '@/lib/labels';
import { usePalette, type Palette } from '@/lib/theme';

/** 趋势图依次取第一个可用的指标。 */
const TREND_METRICS = ['crm.deals_won', 'erp.sales_shipped', 'tasks.completed'];

export default function AnalyticsScreen() {
  const p = usePalette();
  const [range, setRange] = useState<AnalyticsRange>('this_month');
  const comparison = analyticsRanges.find((r) => r.value === range)?.comparison ?? '较上期';
  const overview = useQuery({
    queryKey: ['analytics', 'data', 'overview', range],
    queryFn: ({ signal }) => getApi().analytics.overview(undefined, undefined, range, undefined, signal),
    placeholderData: keepPreviousData,
  });
  const metrics = useQuery({ queryKey: ['analytics', 'metrics'], queryFn: ({ signal }) => getApi().analytics.metrics(signal), staleTime: 5 * 60_000 });
  const trendMetric = TREND_METRICS.find((key) => metrics.data?.some((m) => m.key === key));
  const interval = range === 'this_quarter' ? 'week' : range === 'this_year' ? 'month' : 'day';
  const trend = useQuery({
    queryKey: ['analytics', 'data', 'query', { metric: trendMetric, range, interval }],
    queryFn: ({ signal }) => getApi().analytics.query(trendMetric, undefined, undefined, range, interval, undefined, undefined, undefined, false, undefined, signal),
    enabled: !!trendMetric,
    placeholderData: keepPreviousData,
  });
  const data = overview.data;

  return (
    <ScrollView
      style={{ backgroundColor: p.background }}
      contentContainerStyle={styles.container}
      refreshControl={
        <RefreshControl
          refreshing={overview.isRefetching}
          onRefresh={() => {
            void overview.refetch();
            void trend.refetch();
          }}
        />
      }
    >
      <Stack.Screen options={{ title: '经营分析' }} />
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
        {analyticsRanges.map((r) => (
          <Chip key={r.value} label={r.label} active={r.value === range} onPress={() => setRange(r.value)} />
        ))}
      </ScrollView>
      {data ? (
        <>
          <Muted>
            {formatDate(data.from)} – {formatDate(data.to)} · 按 {data.timeZone} 统计
          </Muted>
          <View style={styles.grid}>
            {data.kpis.map((kpi) => (
              <Kpi key={kpi.metric} kpi={kpi} comparison={comparison} p={p} />
            ))}
          </View>
          {trend.data ? <Trend result={trend.data} p={p} /> : null}
          {data.pipeline?.length ? (
            <Card>
              <Text style={[styles.title, { color: p.text }]}>销售漏斗</Text>
              {data.pipeline.map((stage) => {
                const max = Math.max(...(data.pipeline ?? []).map((s) => s.deals), 1);
                return (
                  <View key={stage.stageId} style={styles.row}>
                    <View style={styles.rowTop}>
                      <Text style={{ color: p.text, flex: 1 }}>{stage.name}</Text>
                      <Muted>
                        {stage.deals} 个 · {formatTotals(stage.amounts)}
                      </Muted>
                    </View>
                    <ProgressBar percent={(stage.deals / max) * 100} />
                  </View>
                );
              })}
            </Card>
          ) : null}
          {data.projects?.length ? (
            <Card>
              <Text style={[styles.title, { color: p.text }]}>项目进度</Text>
              {data.projects.map((project) => (
                <Pressable
                  key={project.projectId}
                  style={styles.row}
                  onPress={() => router.push({ pathname: '/projects/[id]', params: { id: project.projectId } })}
                >
                  <View style={styles.rowTop}>
                    <Text style={{ color: p.text, flex: 1 }} numberOfLines={1}>
                      {project.name}
                    </Text>
                    <Muted style={project.isOverdue ? { color: p.warning } : undefined}>
                      {project.percent}%{project.isOverdue ? ' · 已逾期' : ''}
                    </Muted>
                  </View>
                  <ProgressBar percent={project.percent} />
                  {project.blocked || project.overdue ? (
                    <Muted>{[project.blocked ? `受阻 ${project.blocked}` : '', project.overdue ? `逾期任务 ${project.overdue}` : ''].filter(Boolean).join(' · ')}</Muted>
                  ) : null}
                </Pressable>
              ))}
            </Card>
          ) : null}
          {data.agents?.length ? (
            <Card>
              <Text style={[styles.title, { color: p.text }]}>智能体工作量</Text>
              {data.agents.map((agent) => (
                <View key={agent.agentId} style={styles.rowTop}>
                  <Text style={{ color: p.text, flex: 1 }} numberOfLines={1}>
                    {agent.name}
                  </Text>
                  <Muted>
                    待办 {agent.openTasks} · 执行 {agent.runs}
                    {agent.failedRuns ? ` · 失败 ${agent.failedRuns}` : ''} · ${agent.costUsd.toFixed(2)}
                  </Muted>
                </View>
              ))}
            </Card>
          ) : null}
          {data.lowStock?.length ? (
            <Card>
              <Text style={[styles.title, { color: p.text }]}>库存预警</Text>
              {data.lowStock.map((item) => (
                <Pressable
                  key={item.productId}
                  style={styles.rowTop}
                  onPress={() => router.push({ pathname: '/erp/products/[id]', params: { id: item.productId } })}
                >
                  <Text style={{ color: p.text, flex: 1 }} numberOfLines={1}>
                    {item.sku} {item.name}
                  </Text>
                  <Text style={{ color: p.warning, fontSize: 13 }}>
                    {formatQuantity(item.available, item.unit)} / {formatQuantity(item.reorderPoint, item.unit)}
                  </Text>
                </Pressable>
              ))}
            </Card>
          ) : null}
        </>
      ) : overview.isPending ? null : (
        <Empty title='加载失败' hint='下拉重试。' />
      )}
    </ScrollView>
  );
}

function Kpi({ kpi, comparison, p }: { kpi: AnalyticsKpiDto; comparison: string; p: Palette }) {
  const change = describeChange(kpi.value, kpi.previousValue, kpi.isRatio);
  return (
    <View style={[styles.kpi, { backgroundColor: p.card, borderColor: p.border }]}>
      <Muted>{kpi.name}</Muted>
      <Text style={{ color: p.text, fontSize: 18, fontWeight: '600' }} numberOfLines={1} adjustsFontSizeToFit>
        {formatMetric(kpi.value, kpi.unit, kpi.currency)}
      </Text>
      <Text style={{ fontSize: 12, color: !change || change.up === null ? p.muted : change.up ? p.success : p.danger }} numberOfLines={1}>
        {change ? `${comparison} ${change.text}` : '无可比数据'}
      </Text>
    </View>
  );
}

/** 简单柱状趋势：每个时间段一根柱子，按最大值等比缩放。 */
function Trend({ result, p }: { result: MetricResultDto; p: Palette }) {
  const max = Math.max(...result.points.map((pt) => pt.value ?? 0), 0);
  const first = result.points[0];
  const last = result.points[result.points.length - 1];
  return (
    <Card>
      <View style={styles.rowTop}>
        <Text style={[styles.title, { color: p.text, flex: 1 }]}>{result.name}</Text>
        <Text style={{ color: p.text, fontWeight: '600' }}>{formatMetric(result.total, result.unit, result.currency)}</Text>
      </View>
      <View style={styles.bars} accessibilityLabel={`${result.name}趋势`}>
        {result.points.map((pt) => (
          <View key={pt.date} style={styles.barSlot}>
            <View style={{ height: `${max > 0 ? Math.max(((pt.value ?? 0) / max) * 100, pt.value ? 2 : 0) : 0}%`, backgroundColor: p.primary, borderRadius: 2 }} />
          </View>
        ))}
      </View>
      {first && last ? (
        <View style={styles.rowTop}>
          <Muted style={{ flex: 1 }}>{formatDate(first.date)}</Muted>
          <Muted>{formatDate(last.date)}</Muted>
        </View>
      ) : null}
    </Card>
  );
}

const styles = StyleSheet.create({
  container: { padding: 16, gap: 12 },
  chips: { gap: 8 },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  kpi: { flexBasis: '47%', flexGrow: 1, borderWidth: 1, borderRadius: 12, padding: 12, gap: 4 },
  title: { fontSize: 15, fontWeight: '600', marginBottom: 4 },
  row: { gap: 6, paddingVertical: 4 },
  rowTop: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  bars: { flexDirection: 'row', alignItems: 'flex-end', height: 120, gap: 2, marginVertical: 8 },
  barSlot: { flex: 1, height: '100%', justifyContent: 'flex-end' },
});
