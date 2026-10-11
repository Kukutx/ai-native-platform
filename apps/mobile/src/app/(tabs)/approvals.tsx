import { useState } from 'react';
import { FlatList, Platform, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { Stack } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ApprovalDto } from '@ainative/api-client';
import { Button, Card, Chip, Empty, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { approvalStatus, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ApprovalsScreen() {
  const p = usePalette();
  const [pendingOnly, setPendingOnly] = useState(true);
  const status = pendingOnly ? 'pending' : undefined;
  const approvals = useQuery({ queryKey: ['approvals', status ?? 'all'], queryFn: ({ signal }) => getApi().approvals.list(status, signal) });

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '审批' }} />
      <FlatList
        data={approvals.data ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={approvals.isRefetching} onRefresh={() => void approvals.refetch()} />}
        ListHeaderComponent={
          <View style={styles.filters}>
            <Chip label='待处理' active={pendingOnly} onPress={() => setPendingOnly(true)} />
            <Chip label='全部' active={!pendingOnly} onPress={() => setPendingOnly(false)} />
          </View>
        }
        ListEmptyComponent={approvals.isPending ? null : <Empty title={pendingOnly ? '没有待处理的审批' : '还没有审批记录'} />}
        renderItem={({ item }) => <ApprovalCard approval={item} />}
      />
    </View>
  );
}

function ApprovalCard({ approval }: { approval: ApprovalDto }) {
  const p = usePalette();
  const queryClient = useQueryClient();
  const [note, setNote] = useState('');
  const decide = useMutation({
    mutationFn: (approve: boolean) => getApi().approvals.decide(approval.id, { approve, note: note.trim() || undefined }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['approvals'] }),
  });
  const pending = approval.status === 'pending';

  return (
    <Card>
      <View style={styles.header}>
        <Text style={{ color: p.text, fontSize: 16, fontWeight: '600', flex: 1 }}>{approval.actionTitle}</Text>
        <Muted>{approvalStatus[approval.status]}</Muted>
      </View>
      <Muted>
        {approval.requestedBy.name ?? '系统'} · {formatTime(approval.createdAt)}
      </Muted>
      {approval.rationale ? <Text style={{ color: p.text, lineHeight: 21 }}>{approval.rationale}</Text> : null}
      <Text style={[styles.code, { color: p.muted, backgroundColor: p.bubble }]}>{JSON.stringify(approval.input, null, 2)}</Text>
      {pending ? (
        <View style={styles.actions}>
          <Field label='审批意见（可选）' value={note} onChangeText={setNote} autoCapitalize='sentences' />
          <View style={styles.buttons}>
            <Button title='批准并执行' onPress={() => decide.mutate(true)} loading={decide.isPending} style={styles.flex} />
            <Button title='驳回' variant='outline' onPress={() => decide.mutate(false)} disabled={decide.isPending} style={styles.flex} />
          </View>
        </View>
      ) : (
        <>
          {approval.decisionNote ? <Muted>意见：{approval.decisionNote}</Muted> : null}
          {approval.executionError ? <Text style={{ color: p.danger }}>执行失败：{approval.executionError}</Text> : null}
        </>
      )}
    </Card>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 12 },
  filters: { flexDirection: 'row', gap: 8 },
  header: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  code: { fontSize: 12, padding: 10, borderRadius: 8, fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) },
  actions: { gap: 10, marginTop: 4 },
  buttons: { flexDirection: 'row', gap: 10 },
});
