import { useState } from 'react';
import { KeyboardAvoidingView, Platform, Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery } from '@tanstack/react-query';
import { sessionCan, type WorkflowStep } from '@ainative/api-client';
import { Button, Card, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatTime, workflowRunStatus, workflowStepKind, workflowTriggerKind } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function WorkflowDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const [inputText, setInputText] = useState('');
  const [inputError, setInputError] = useState<string | null>(null);
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['workflows', id], queryFn: ({ signal }) => getApi().workflows.get(id, signal) });
  const runs = useQuery({
    queryKey: ['workflow-runs', { workflowId: id }],
    queryFn: ({ signal }) => getApi().workflows.runs(id, undefined, 1, 20, signal),
  });
  const start = useMutation({
    mutationFn: (input: unknown) => getApi().workflows.start(id, { input }),
    onSuccess: (run) => router.push({ pathname: '/workflows/runs/[id]', params: { id: run.id } }),
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const workflow = detail.data;
  // 步骤里引用了 input.xxx 时才需要填写运行参数。
  const needsInput = JSON.stringify(workflow.steps).includes('input.');
  const run = () => {
    let input: unknown = {};
    if (inputText.trim()) {
      try {
        input = JSON.parse(inputText);
      } catch {
        setInputError('不是合法的 JSON');
        return;
      }
    }

    if (typeof input !== 'object' || input === null || Array.isArray(input)) {
      setInputError('运行参数必须是 JSON 对象');
      return;
    }

    setInputError(null);
    start.mutate(input);
  };

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: workflow.name }} />
      <ScrollView
        contentContainerStyle={styles.container}
        keyboardShouldPersistTaps='handled'
        refreshControl={<RefreshControl refreshing={detail.isRefetching} onRefresh={() => void Promise.all([detail.refetch(), runs.refetch()])} />}
      >
        <Text style={[styles.title, { color: p.text }]}>{workflow.name}</Text>
        <Muted>
          {workflow.isEnabled ? '已启用' : '已停用'} · {workflowTriggerKind[workflow.trigger.kind ?? 'manual']}
          {workflow.nextRunAt ? ` · 下次 ${formatTime(workflow.nextRunAt)}` : ''}
        </Muted>
        {workflow.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{workflow.description}</Text> : null}
        <Muted>以「{workflow.owner.name ?? '—'}」的身份运行</Muted>

        <Card>
          <Text style={[styles.section, { color: p.text }]}>步骤</Text>
          <StepLines steps={workflow.steps} depth={0} />
        </Card>

        {sessionCan(session.data, 'workflows.run') ? (
          <View style={styles.gap}>
            {needsInput ? (
              <Field
                label='运行参数（JSON）'
                value={inputText}
                onChangeText={setInputText}
                multiline
                placeholder='{"customer": "星火科技"}'
                style={{ fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) }}
              />
            ) : null}
            {inputError ? <Text style={{ color: p.danger }}>{inputError}</Text> : null}
            <Button title='立即运行' onPress={run} loading={start.isPending} />
          </View>
        ) : null}

        <Text style={[styles.section, { color: p.text }]}>运行记录</Text>
        {runs.data?.items.length ? (
          runs.data.items.map((r) => (
            <Pressable
              key={r.id}
              onPress={() => router.push({ pathname: '/workflows/runs/[id]', params: { id: r.id } })}
              style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}
            >
              <View style={styles.row}>
                <Text style={{ color: p.text, fontWeight: '500' }}>{workflowRunStatus[r.status]}</Text>
                <Muted>{formatTime(r.startedAt ?? r.createdAt)}</Muted>
              </View>
              <Muted>
                {workflowTriggerKind[r.triggerKind]}
                {r.triggeredBy.name ? ` · ${r.triggeredBy.name}` : ''} · {Math.min(r.currentStep, r.stepCount)}/{r.stepCount}
              </Muted>
              {r.error ? (
                <Text style={{ color: p.danger }} numberOfLines={2}>
                  {r.error}
                </Text>
              ) : null}
            </Pressable>
          ))
        ) : (
          <Muted>还没有运行过。</Muted>
        )}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

/** 步骤定义，分支和遍历里的步骤缩进列在下面。 */
function StepLines({ steps, depth }: { steps: WorkflowStep[]; depth: number }) {
  const p = usePalette();
  return steps.map((step, index) => (
    <View key={step.key} style={{ marginLeft: depth * 14 }}>
      <Text style={{ color: p.text, lineHeight: 22 }}>
        {index + 1}. {step.name}
        <Text style={{ color: p.muted }}> · {workflowStepKind[step.kind ?? 'action']}</Text>
      </Text>
      {step.kind === 'branch'
        ? step.branches?.map((branch, b) => (
            <View key={b} style={{ marginLeft: (depth + 1) * 14 }}>
              <Muted>
                {branch.name}：{branch.when || '其他情况'}
              </Muted>
              <StepLines steps={branch.steps ?? []} depth={1} />
            </View>
          ))
        : null}
      {step.kind === 'for_each' ? (
        <View style={{ marginLeft: (depth + 1) * 14 }}>
          <Muted>遍历 {step.items}</Muted>
          <StepLines steps={step.steps ?? []} depth={1} />
        </View>
      ) : null}
    </View>
  ));
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  gap: { gap: 10 },
  title: { fontSize: 22, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600', marginTop: 4 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 4 },
  row: { flexDirection: 'row', justifyContent: 'space-between', gap: 8 },
});
