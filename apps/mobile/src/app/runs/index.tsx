import { useState } from 'react';
import { ActivityIndicator, FlatList, RefreshControl, ScrollView, StyleSheet, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { toProblem } from '@ainative/api-client';
import { RunRow } from '@/components/runs';
import { Chip, Empty } from '@/components/ui';
import { getApi } from '@/lib/api';
import { useCan } from '@/lib/session';
import { usePalette } from '@/lib/theme';

const PAGE_SIZE = 30;

/** 执行记录：智能体每次被唤醒留下的一条记录，可以按智能体筛选，滑到底自动加载更多。 */
export default function RunsScreen() {
  const p = usePalette();
  const can = useCan();
  const params = useLocalSearchParams<{ agentId?: string }>();
  const [agentId, setAgentId] = useState<string | undefined>(params.agentId);
  const agents = useQuery({ queryKey: ['agents'], queryFn: ({ signal }) => getApi().agents.list(signal), enabled: can('agents.view') });
  const runs = useInfiniteQuery({
    queryKey: ['runs', agentId ?? 'all'],
    queryFn: ({ pageParam, signal }) => getApi().runs.list(agentId, pageParam, PAGE_SIZE, signal),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });
  const items = runs.data?.pages.flatMap((page) => page.items) ?? [];
  const choices = agents.data?.filter((a) => a.status !== 'terminated' || a.id === agentId) ?? [];

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '执行记录' }} />
      <FlatList
        data={items}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={runs.isRefetching && !runs.isFetchingNextPage} onRefresh={() => void runs.refetch()} />}
        onEndReached={() => {
          if (runs.hasNextPage && !runs.isFetchingNextPage) {
            void runs.fetchNextPage();
          }
        }}
        onEndReachedThreshold={0.5}
        ListHeaderComponent={
          choices.length ? (
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips}>
              <Chip label='全部智能体' active={!agentId} onPress={() => setAgentId(undefined)} />
              {choices.map((a) => (
                <Chip key={a.id} label={a.name} active={agentId === a.id} onPress={() => setAgentId(a.id)} />
              ))}
            </ScrollView>
          ) : null
        }
        ListEmptyComponent={
          runs.isPending ? (
            <ActivityIndicator style={styles.loading} color={p.muted} />
          ) : runs.isError ? (
            <Empty title='加载失败' hint={toProblem(runs.error).title} />
          ) : (
            <Empty title='还没有执行记录' hint='给智能体指派任务或手动唤醒后，这里会出现它的执行过程。' />
          )
        }
        ListFooterComponent={runs.isFetchingNextPage ? <ActivityIndicator color={p.muted} /> : null}
        renderItem={({ item }) => <RunRow run={item} showAgent={!agentId} />}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 10 },
  chips: { gap: 8, paddingBottom: 4 },
  loading: { marginTop: 48 },
});
