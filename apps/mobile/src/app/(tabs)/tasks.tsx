import { useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import Ionicons from '@expo/vector-icons/Ionicons';
import { Chip, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { priority, workItemStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function TasksScreen() {
  const p = usePalette();
  const [openOnly, setOpenOnly] = useState(true);
  const tasks = useQuery({
    queryKey: ['work-items', { openOnly }],
    queryFn: ({ signal }) => getApi().workItems.list(undefined, undefined, undefined, undefined, openOnly, 1, 100, signal),
  });
  const agents = useQuery({ queryKey: ['agents'], queryFn: ({ signal }) => getApi().agents.list(signal) });
  const agentNames = new Map(agents.data?.map((a) => [a.id, a.name]));

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen
        options={{
          title: '任务',
          headerRight: () => (
            <Pressable onPress={() => router.push('/tasks/new')} accessibilityLabel='新建任务' style={styles.headerButton}>
              <Ionicons name='add' size={26} color={p.text} />
            </Pressable>
          ),
        }}
      />
      <FlatList
        data={tasks.data?.items ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={tasks.isRefetching} onRefresh={() => void tasks.refetch()} />}
        ListHeaderComponent={
          <View style={styles.filters}>
            <Chip label='未完成' active={openOnly} onPress={() => setOpenOnly(true)} />
            <Chip label='全部' active={!openOnly} onPress={() => setOpenOnly(false)} />
          </View>
        }
        ListEmptyComponent={tasks.isPending ? null : <Empty title='没有任务' hint='点右上角新建任务，指派给智能体后它会自动开始处理。' />}
        renderItem={({ item }) => (
          <Pressable
            onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: item.id } })}
            style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}
          >
            <View style={styles.cardTop}>
              <Muted>{item.key}</Muted>
              <Muted>{item.checkoutRunId ? '执行中 · ' : ''}{workItemStatus[item.status]}</Muted>
            </View>
            <Text style={{ color: p.text, fontSize: 15, fontWeight: '500' }} numberOfLines={2}>
              {item.title}
            </Text>
            <View style={styles.cardTop}>
              <Muted>{item.assigneeAgentId ? (agentNames.get(item.assigneeAgentId) ?? '智能体') : '未指派'}</Muted>
              <Muted>优先级 {priority[item.priority]}</Muted>
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
  filters: { flexDirection: 'row', gap: 8, marginBottom: 4 },
  headerButton: { paddingHorizontal: 12 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  cardTop: { flexDirection: 'row', justifyContent: 'space-between' },
});
