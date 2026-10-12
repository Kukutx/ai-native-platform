import { FlatList, Pressable, RefreshControl, StyleSheet, Switch, Text, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatTime, workflowRunStatus, workflowTriggerKind } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function WorkflowsScreen() {
  const p = usePalette();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const workflows = useQuery({ queryKey: ['workflows'], queryFn: ({ signal }) => getApi().workflows.list(signal) });
  const setEnabled = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => getApi().workflows.setEnabled(id, { enabled }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['workflows'] }),
  });
  const manage = sessionCan(session.data, 'workflows.manage');

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '自动化' }} />
      <FlatList
        data={workflows.data ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={workflows.isRefetching} onRefresh={() => void workflows.refetch()} />}
        ListEmptyComponent={workflows.isPending ? null : <Empty title='还没有工作流' hint='在网页端或让智能体在对话里搭建工作流，这里可以查看、启停和手动运行。' />}
        renderItem={({ item }) => (
          <Pressable
            onPress={() => router.push({ pathname: '/workflows/[id]', params: { id: item.id } })}
            style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}
          >
            <View style={styles.row}>
              <Text style={{ color: p.text, fontSize: 16, fontWeight: '600', flex: 1 }} numberOfLines={1}>
                {item.name}
              </Text>
              <Switch
                value={item.isEnabled}
                disabled={!manage || setEnabled.isPending}
                onValueChange={(enabled) => setEnabled.mutate({ id: item.id, enabled })}
                trackColor={{ false: p.border, true: p.primary }}
                thumbColor={p.card}
                accessibilityLabel={item.isEnabled ? '停用' : '启用'}
              />
            </View>
            {item.description ? <Muted>{item.description}</Muted> : null}
            <View style={styles.row}>
              <Muted>
                {workflowTriggerKind[item.trigger.kind ?? 'manual']} · {item.steps.length} 步
              </Muted>
              <Muted>{item.lastRun ? `${workflowRunStatus[item.lastRun.status]} · ${formatTime(item.lastRun.createdAt)}` : '从未运行'}</Muted>
            </View>
          </Pressable>
        )}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 10 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
});
