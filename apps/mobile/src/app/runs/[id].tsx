import { useEffect, useMemo, useState } from 'react';
import { ActivityIndicator, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toProblem, type RunEventDto } from '@ainative/api-client';
import { RunTimeline } from '@/components/runs';
import { Card, Empty, Muted, Status } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatTime, formatUsd, runFinished, runStatus, wakeupSource } from '@/lib/labels';
import { getRealtime } from '@/lib/realtime';
import { useCan } from '@/lib/session';
import { usePalette } from '@/lib/theme';

/** 执行详情（只读）：概览、唤醒原因和过程时间线；执行中时通过实时通道逐条追加过程和逐字输出。 */
export default function RunScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const can = useCan();
  const detail = useQuery({ queryKey: ['run', id], queryFn: ({ signal }) => getApi().runs.get(id, signal) });
  const [live, setLive] = useState<{ events: RunEventDto[]; text: string }>({ events: [], text: '' });
  const active = !!detail.data && !runFinished(detail.data.run.status);

  useEffect(() => {
    if (!active) {
      return;
    }

    // 实时通道由根布局负责连接和重连；这里只订阅这次运行，连接就绪（或重连）后自动加入。
    const realtime = getRealtime();
    const off = realtime.onRunEvent((event) => {
      if (event.runId !== id) {
        return;
      }

      if (event.seq === 0) {
        // 序号 0 是逐字增量，不落库；收到下一条持久化事件时清空。
        const delta = (event.data as { delta?: string } | undefined)?.delta ?? '';
        setLive((s) => ({ ...s, text: s.text + delta }));
        return;
      }

      setLive((s) => ({ events: [...s.events.filter((e) => e.seq !== event.seq), event], text: '' }));
      const status = (event.data as { status?: string } | undefined)?.status;
      if (event.type === 'error' || (event.type === 'status' && status && runFinished(status))) {
        void queryClient.invalidateQueries({ queryKey: ['run', id] });
      }
    });
    realtime.subscribeRun(id).catch(() => {
      // 订阅失败时依赖实体变化通知刷新详情。
    });

    return () => {
      off();
      realtime.unsubscribeRun(id).catch(() => {});
    };
  }, [id, active, queryClient]);

  const events = useMemo(() => {
    const merged = new Map<number, RunEventDto>();
    for (const event of [...(detail.data?.events ?? []), ...live.events]) {
      merged.set(event.seq, event);
    }

    return [...merged.values()].sort((a, b) => a.seq - b.seq);
  }, [detail.data, live.events]);

  if (!detail.data) {
    return (
      <View style={[styles.flex, { backgroundColor: p.background }]}>
        {detail.isError ? <Empty title='加载失败' hint={toProblem(detail.error).title} /> : <ActivityIndicator style={styles.loading} color={p.muted} />}
      </View>
    );
  }

  const { run, wakeup } = detail.data;
  const status = runStatus[run.status];

  return (
    <ScrollView
      style={{ backgroundColor: p.background }}
      contentContainerStyle={styles.container}
      refreshControl={<RefreshControl refreshing={detail.isRefetching} onRefresh={() => void detail.refetch()} />}
    >
      <Stack.Screen options={{ title: '执行详情' }} />
      <View style={styles.row}>
        <Text style={[styles.title, { color: p.text }]} numberOfLines={2}>
          {can('agents.view') ? (
            <Text style={{ textDecorationLine: 'underline' }} onPress={() => router.push({ pathname: '/agents/[id]', params: { id: run.agentId } })}>
              {run.agentName}
            </Text>
          ) : (
            run.agentName
          )}
          <Text style={{ color: p.muted }}> 的一次执行</Text>
        </Text>
        <Status label={status.label} tone={status.tone} />
      </View>
      {run.error ? <Text style={{ color: p.danger, lineHeight: 21 }}>{run.error}</Text> : null}
      {run.summary && !run.error ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{run.summary}</Text> : null}

      <Card>
        <Text style={[styles.section, { color: p.text }]}>概览</Text>
        <Muted>
          模型：{run.provider} / {run.model}
        </Muted>
        <Muted>
          {run.steps} 步 · 输入 {run.inputTokens.toLocaleString()} tokens · 输出 {run.outputTokens.toLocaleString()} tokens
        </Muted>
        <Muted>花费：{formatUsd(run.costUsd)}</Muted>
        <Muted>
          {formatTime(run.startedAt ?? run.createdAt)}
          {run.finishedAt ? ` → ${formatTime(run.finishedAt)}` : ''}
        </Muted>
        {run.workItemId && can('tasks.view') ? (
          <Text style={{ color: p.text, textDecorationLine: 'underline' }} onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: run.workItemId! } })}>
            查看关联任务
          </Text>
        ) : null}
      </Card>

      {wakeup ? (
        <Card>
          <Text style={[styles.section, { color: p.text }]}>唤醒原因</Text>
          {wakeup.items.map((item, index) => (
            <View key={index} style={styles.wakeup}>
              <Muted>
                {wakeupSource[item.source] ?? item.source} · {formatTime(item.requestedAt)}
              </Muted>
              <Text style={{ color: p.text, lineHeight: 21 }}>{item.reason}</Text>
            </View>
          ))}
          {wakeup.coalescedCount > 0 ? <Muted>{wakeup.coalescedCount + 1} 次唤醒合并成了这一次执行。</Muted> : null}
        </Card>
      ) : null}

      <Text style={[styles.section, { color: p.text }]}>过程</Text>
      {events.length || live.text ? (
        <RunTimeline
          events={events}
          liveText={active ? live.text : undefined}
          onOpenApprovals={can('approvals.view') ? () => router.push('/approvals') : undefined}
        />
      ) : (
        <Muted>{active ? '等待智能体开始…' : '没有记录到过程事件。'}</Muted>
      )}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  loading: { marginTop: 48 },
  container: { padding: 16, gap: 12 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  title: { flex: 1, fontSize: 20, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600' },
  wakeup: { gap: 2 },
});
