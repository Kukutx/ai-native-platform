import { Platform, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan, type WorkflowRunStepDto, type WorkflowStep } from '@ainative/api-client';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { confirm } from '@/lib/confirm';
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
  const stepRuns = new Map(run.stepRuns.map((s) => [s.path, s]));
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

      {rowsFor(run.steps, (i) => `${i}`, stepRuns, 0).map((row) =>
        row.kind === 'item' ? (
          <Text key={row.key} style={{ color: p.muted, marginLeft: row.depth * 14, fontWeight: '600' }} numberOfLines={1}>
            第 {row.number} 项 · {row.preview}
          </Text>
        ) : (
          <StepRunCard key={row.key} row={row} finished={finished} />
        ),
      )}

      {!finished && sessionCan(session.data, 'workflows.run') ? (
        <Button
          title='取消运行'
          variant='danger'
          loading={cancel.isPending}
          onPress={() =>
            confirm({
              title: '取消这次运行？',
              message: '已完成的步骤不会回滚。',
              confirmText: '取消运行',
              cancelText: '不取消',
              destructive: true,
              onConfirm: () => cancel.mutate(),
            })
          }
        />
      ) : null}
    </ScrollView>
  );
}

type Row =
  | { kind: 'step'; key: string; depth: number; number: number; step: WorkflowStep; stepRun?: WorkflowRunStepDto; note?: string }
  | { kind: 'item'; key: string; depth: number; number: number; preview: string };

/** 把嵌套的步骤摊平成带缩进的行：分支只展开走到的那一支，遍历按项展开已开始的项。 */
function rowsFor(steps: WorkflowStep[], pathOf: (index: number) => string, stepRuns: Map<string, WorkflowRunStepDto>, depth: number): Row[] {
  return steps.flatMap((step, index): Row[] => {
    const path = pathOf(index);
    const stepRun = stepRuns.get(path);
    const rows: Row[] = [];
    if (step.kind === 'branch' && stepRun) {
      const chosen = typeof stepRun.input?.branch === 'number' ? (stepRun.input.branch as number) : -1;
      const branch = chosen >= 0 ? step.branches?.[chosen] : undefined;
      rows.push({ kind: 'step', key: path, depth, number: index + 1, step, stepRun, note: branch ? `走了「${branch.name}」` : stepRun.status === 'succeeded' ? '没有成立的分支' : undefined });
      if (branch) {
        rows.push(...rowsFor(branch.steps ?? [], (i) => `${path}.${chosen}.${i}`, stepRuns, depth + 1));
      }

      return rows;
    }

    if (step.kind === 'for_each' && stepRun) {
      const items = Array.isArray(stepRun.input?.items) ? (stepRun.input.items as unknown[]) : [];
      rows.push({ kind: 'step', key: path, depth, number: index + 1, step, stepRun, note: `共 ${items.length} 项` });
      items.forEach((item, i) => {
        if (stepRuns.has(`${path}.${i}.0`)) {
          const text = typeof item === 'string' ? item : JSON.stringify(item);
          rows.push({ kind: 'item', key: `${path}.${i}`, depth: depth + 1, number: i + 1, preview: text.length > 60 ? `${text.slice(0, 60)}…` : text });
          rows.push(...rowsFor(step.steps ?? [], (j) => `${path}.${i}.${j}`, stepRuns, depth + 1));
        }
      });
      return rows;
    }

    return [{ kind: 'step', key: path, depth, number: index + 1, step, stepRun }];
  });
}

function StepRunCard({ row, finished }: { row: Extract<Row, { kind: 'step' }>; finished: boolean }) {
  const p = usePalette();
  const { step, stepRun } = row;
  const control = step.kind === 'branch' || step.kind === 'for_each';
  const text = typeof stepRun?.output?.text === 'string' ? stepRun.output.text : typeof stepRun?.output?.result === 'string' ? stepRun.output.result : null;
  return (
    <View style={{ marginLeft: row.depth * 14 }}>
      <Card>
        <View style={styles.row}>
          <Text style={{ color: p.text, fontWeight: '600', flex: 1 }}>
            {row.number}. {step.name}
          </Text>
          <Muted>{stepRun ? workflowStepStatus[stepRun.status] : finished ? '未执行' : '待执行'}</Muted>
        </View>
        <Muted>
          {workflowStepKind[step.kind ?? 'action']}
          {row.note ? ` · ${row.note}` : ''}
        </Muted>
        {stepRun?.error ? <Text style={{ color: p.danger }}>{stepRun.error}</Text> : null}
        {text ? <Text style={{ color: p.text, lineHeight: 21 }}>{text}</Text> : null}
        {!control && stepRun?.output !== undefined && stepRun.output !== null && !text ? (
          <Text style={[styles.code, { color: p.muted, backgroundColor: p.bubble }]} numberOfLines={12}>
            {JSON.stringify(stepRun.output, null, 2)}
          </Text>
        ) : null}
      </Card>
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  code: { fontSize: 12, padding: 10, borderRadius: 8, fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) },
});
