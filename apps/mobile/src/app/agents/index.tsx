import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { toProblem } from '@ainative/api-client';
import { Empty, Muted, Status } from '@/components/ui';
import { getApi } from '@/lib/api';
import { agentStatus, formatUsd } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function AgentsScreen() {
  const p = usePalette();
  const agents = useQuery({ queryKey: ['agents'], queryFn: ({ signal }) => getApi().agents.list(signal) });
  // 已终止的智能体不再工作，和 Web 端一样不在列表里显示。
  const active = agents.data?.filter((a) => a.status !== 'terminated') ?? [];

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '智能体' }} />
      <FlatList
        data={active}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={agents.isRefetching} onRefresh={() => void agents.refetch()} />}
        ListEmptyComponent={
          agents.isPending ? null : agents.isError ? (
            <Empty title='加载失败' hint={toProblem(agents.error).title} />
          ) : (
            <Empty title='还没有智能体' hint='在网页端招聘智能体，或者在对话里让首席助理帮你招一位。' />
          )
        }
        renderItem={({ item }) => {
          const status = agentStatus[item.status];
          return (
            <Pressable
              onPress={() => router.push({ pathname: '/agents/[id]', params: { id: item.id } })}
              accessibilityRole='button'
              style={({ pressed }) => [styles.card, { backgroundColor: p.card, borderColor: p.border }, pressed && { opacity: 0.6 }]}
            >
              <View style={styles.row}>
                <View style={styles.flex}>
                  <Text style={{ color: p.text, fontSize: 16, fontWeight: '600' }} numberOfLines={1}>
                    {item.name}
                  </Text>
                  {item.title ? <Muted>{item.title}</Muted> : null}
                </View>
                <Status label={status.label} tone={status.tone} />
              </View>
              {item.status === 'paused' && item.pauseReason ? <Text style={{ color: p.warning }}>{item.pauseReason}</Text> : null}
              {item.status === 'error' && item.lastError ? (
                <Text style={{ color: p.danger }} numberOfLines={2}>
                  {item.lastError}
                </Text>
              ) : null}
              <Muted>
                {item.model} · 本月 {formatUsd(item.spentThisMonthUsd)}
                {item.monthlyBudgetUsd > 0 ? ` / ${formatUsd(item.monthlyBudgetUsd)}` : ''}
              </Muted>
            </Pressable>
          );
        }}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 10 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
});
