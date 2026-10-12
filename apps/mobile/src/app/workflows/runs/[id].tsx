import { Alert, Platform, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatTime, workflowRunStatus, workflowStepKind, workflowStepStatus, workflowTriggerKind } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

const active = new Set(['queued', 'running', 'waiting']);

export default function WorkflowRunScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  // 进度由实时通道推送（workflow_run 变化）后失效重取，不轮询。
  const detail = useQuery({ queryKey: ['workflow-run', id], queryFn: ({ signal }) => getApi().workflows.run(id, signal) });
  const cancel = useMutation({
    mutationFn: () => getApi().workflows.cancel(id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['workflow-run', id] }),
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const run = detail.data;
  const { summary } = run;
  const stepRuns = new Map(run.stepRuns.map((s) => [s.index, s]));
  const finished = !active.has(summary.status);

  return (
    <ScrollView
      style={{ backgroundColor: p.background }}
      contentContainerStyle={styles.container}
      refreshControl={<RefreshControl refreshing={detail.isRefetching} onRefresh={() => void detail.refetch()} />}
    >
      <Stack.Screen options={{ title: '运行详情' }} />
      <Text style={[styles.title, { color: p.text }]}>{summary.workflowName}</Text>
      <Muted>
        {workflowRunStatus[summary.status]} · {workflowTriggerKind[summary.triggerKind]}
        {summary.triggeredBy.name ? ` · ${summary.triggeredBy.name}` : ''}
      </Muted>
      <Muted>
        {formatTime(summary.startedAt ?? summary.createdAt)}
        {summary.finishedAt ? ` → ${formatTime(summary.finishedAt)}` : ''}
      </Muted>
      {summary.error ? <Text style={{ color: p.danger, lineHeight: 21 }}>{summary.error}</Text> : null}

      {summary.status === 'waiting' && run.waitingApprovalId ? (
        <Button title='等待审批，去处理' variant='outline' onPress={() => router.push('/approvals')} />
      ) : null}
      {summary.status === 'waiting' && run.waitingWorkItemId ? (
        <Button
          title='等待智能体完成任务，查看任务'
          variant='outline'
          onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: run.waitingWorkItemId! } })}
        />
      ) : null}

      {run.steps.map((step, index) => {
        const stepRun = stepRuns.get(index);
        const text = typeof stepRun?.output?.text === 'string' ? stepRun.output.text : typeof stepRun?.output?.result === 'string' ? stepRun.output.result : null;
        return (
          <Card key={step.key}>
            <View style={styles.row}>
              <Text style={{ color: p.text, fontWeight: '600', flex: 1 }}>
                {index + 1}. {step.name}
              </Text>
              <Muted>{stepRun ? workflowStepStatus[stepRun.status] : finished ? '未执行' : '待执行'}</Muted>
            </View>
            <Muted>{workflowStepKind[step.kind ?? 'action']}</Muted>
            {stepRun?.error ? <Text style={{ color: p.danger }}>{stepRun.error}</Text> : null}
            {text ? <Text style={{ color: p.text, lineHeight: 21 }}>{text}</Text> : null}
            {stepRun?.output !== undefined && stepRun.output !== null && !text ? (
              <Text style={[styles.code, { color: p.muted, backgroundColor: p.bubble }]} numberOfLines={12}>
                {JSON.stringify(stepRun.output, null, 2)}
              </Text>
            ) : null}
          </Card>
        );
      })}

      {!finished && sessionCan(session.data, 'workflows.run') ? (
        <Button
          title='取消运行'
          variant='danger'
          loading={cancel.isPending}
          onPress={() =>
            Platform.OS === 'web'
              ? cancel.mutate()
              : Alert.alert('取消这次运行？', '已完成的步骤不会回滚。', [
                  { text: '不取消', style: 'cancel' },
                  { text: '取消运行', style: 'destructive', onPress: () => cancel.mutate() },
                ])
          }
        />
      ) : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  code: { fontSize: 12, padding: 10, borderRadius: 8, fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) },
});
