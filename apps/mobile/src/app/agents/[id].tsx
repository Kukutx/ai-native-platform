import { ActivityIndicator, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toProblem, type AgentDto } from '@ainative/api-client';
import { LimitNote, LinkRow } from '@/components/crm';
import { ProgressBar } from '@/components/progress';
import { RunRow } from '@/components/runs';
import { Button, Card, Empty, Muted, Status } from '@/components/ui';
import { getApi } from '@/lib/api';
import { agentStatus, formatTime, formatUsd, workItemStatus } from '@/lib/labels';
import { useCan } from '@/lib/session';
import { usePalette } from '@/lib/theme';

const RECENT_RUNS = 10;

function heartbeat(seconds: number) {
  if (seconds <= 0) {
    return '关闭';
  }

  if (seconds % 3600 === 0) {
    return `每 ${seconds / 3600} 小时`;
  }

  return seconds % 60 === 0 ? `每 ${seconds / 60} 分钟` : `每 ${seconds} 秒`;
}

/** 智能体详情（只读）：状态、岗位职责、本月花费和最近的执行记录。 */
export default function AgentScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const can = useCan();
  const detail = useQuery({
    queryKey: ['agents', id],
    queryFn: ({ signal }) => getApi().agents.get(id, signal),
    // 从列表进来时先用列表里的数据显示，后台再取最新的。
    initialData: () => queryClient.getQueryData<AgentDto[]>(['agents'])?.find((a) => a.id === id),
    initialDataUpdatedAt: () => queryClient.getQueryState(['agents'])?.dataUpdatedAt,
  });
  const agents = useQuery({ queryKey: ['agents'], queryFn: ({ signal }) => getApi().agents.list(signal) });
  const tasks = useQuery({
    queryKey: ['work-items', { assigneeAgentId: id, openOnly: true }],
    queryFn: ({ signal }) => getApi().workItems.list(undefined, id, undefined, undefined, true, 1, 20, undefined, undefined, undefined, undefined, signal),
    enabled: can('tasks.view'),
  });
  const runs = useQuery({
    queryKey: ['runs', id, 'recent'],
    queryFn: ({ signal }) => getApi().runs.list(id, 1, RECENT_RUNS, signal),
    enabled: can('runs.view'),
  });

  if (!detail.data) {
    return (
      <View style={[styles.flex, { backgroundColor: p.background }]}>
        {detail.isError ? <Empty title='加载失败' hint={toProblem(detail.error).title} /> : <ActivityIndicator style={styles.loading} color={p.muted} />}
      </View>
    );
  }

  const agent = detail.data;
  const status = agentStatus[agent.status];
  const manager = agent.reportsToId ? agents.data?.find((a) => a.id === agent.reportsToId) : undefined;
  const budget = agent.monthlyBudgetUsd;

  return (
    <ScrollView
      style={{ backgroundColor: p.background }}
      contentContainerStyle={styles.container}
      refreshControl={
        <RefreshControl
          refreshing={detail.isRefetching}
          onRefresh={() => {
            // refetch 不看 enabled，没有权限的部分不刷新，免得请求被拒。
            void detail.refetch();
            if (can('tasks.view')) {
              void tasks.refetch();
            }

            if (can('runs.view')) {
              void runs.refetch();
            }
          }}
        />
      }
    >
      <Stack.Screen options={{ title: agent.name }} />
      <View style={styles.row}>
        <View style={styles.flex}>
          <Text style={[styles.title, { color: p.text }]}>{agent.name}</Text>
          {agent.title ? <Muted>{agent.title}</Muted> : null}
        </View>
        <Status label={status.label} tone={status.tone} />
      </View>
      {agent.status === 'paused' && agent.pauseReason ? <Text style={{ color: p.warning, lineHeight: 21 }}>暂停原因：{agent.pauseReason}</Text> : null}
      {agent.lastError ? <Text style={{ color: p.danger, lineHeight: 21 }}>最近一次出错：{agent.lastError}</Text> : null}

      <Card>
        <Text style={[styles.section, { color: p.text }]}>本月花费</Text>
        <Text style={{ color: p.text, fontSize: 20, fontWeight: '700' }}>
          {formatUsd(agent.spentThisMonthUsd)}
          <Text style={{ color: p.muted, fontSize: 14, fontWeight: '400' }}>{budget > 0 ? ` / 预算 ${formatUsd(budget)}` : ' · 不限预算'}</Text>
        </Text>
        {budget > 0 ? <ProgressBar percent={(agent.spentThisMonthUsd / budget) * 100} /> : null}
      </Card>

      <Card>
        <Text style={[styles.section, { color: p.text }]}>概览</Text>
        <Muted>
          模型：{agent.modelProvider} / {agent.model}
        </Muted>
        <Muted>上级：{agent.reportsToId ? (manager?.name ?? '—') : '无'}</Muted>
        <Muted>
          定时心跳：{heartbeat(agent.heartbeatIntervalSeconds)}
          {agent.lastHeartbeatAt ? ` · 上次 ${formatTime(agent.lastHeartbeatAt)}` : ''}
        </Muted>
        <Muted>权限：{agent.permissions.length ? agent.permissions.join('、') : '无'}</Muted>
        {agent.approvalRequiredActions.length ? <Muted>需要审批的动作：{agent.approvalRequiredActions.join('、')}</Muted> : null}
        <Muted>加入于 {formatTime(agent.createdAt)}</Muted>
      </Card>

      <Card>
        <Text style={[styles.section, { color: p.text }]}>岗位职责</Text>
        <Text style={{ color: agent.instructions ? p.text : p.muted, fontSize: 15, lineHeight: 22 }}>{agent.instructions || '（未填写岗位职责）'}</Text>
      </Card>

      {can('tasks.view') && tasks.data?.items.length ? (
        <Card>
          <Text style={[styles.section, { color: p.text }]}>负责中的任务</Text>
          {tasks.data.items.map((task) => (
            <LinkRow key={task.id} title={`${task.key} ${task.title}`} subtitle={workItemStatus[task.status]} href={{ pathname: '/tasks/[id]', params: { id: task.id } }} />
          ))}
          <LimitNote shown={tasks.data.items.length} total={tasks.data.total} />
        </Card>
      ) : null}

      {can('runs.view') ? (
        <View style={styles.runs}>
          <Text style={[styles.section, { color: p.text }]}>最近执行</Text>
          {runs.data?.items.length ? (
            runs.data.items.map((run) => <RunRow key={run.id} run={run} />)
          ) : runs.isPending ? null : (
            <Muted>还没有执行记录。给它指派任务后，它会醒来工作并在这里留下记录。</Muted>
          )}
          {runs.data && runs.data.total > runs.data.items.length ? (
            <Button
              title={`查看全部 ${runs.data.total} 条`}
              variant='outline'
              onPress={() => router.push({ pathname: '/runs', params: { agentId: agent.id } })}
            />
          ) : null}
        </View>
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  loading: { marginTop: 48 },
  container: { padding: 16, gap: 12 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  title: { fontSize: 22, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600' },
  runs: { gap: 10, marginTop: 4 },
});
