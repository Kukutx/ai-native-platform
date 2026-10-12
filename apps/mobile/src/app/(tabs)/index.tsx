import { FlatList, Pressable, RefreshControl, StyleSheet, Text, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ConversationDto } from '@ainative/api-client';
import { TabGuard } from '@/components/tab-guard';
import { Button, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { confirm } from '@/lib/confirm';
import { formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ConversationsTab() {
  return (
    <TabGuard tab='index'>
      <ConversationsScreen />
    </TabGuard>
  );
}

function ConversationsScreen() {
  const p = usePalette();
  const queryClient = useQueryClient();
  const conversations = useQuery({ queryKey: ['conversations'], queryFn: ({ signal }) => getApi().conversations.list(signal) });
  const remove = useMutation({
    mutationFn: (id: string) => getApi().conversations.delete(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['conversations'] }),
  });
  const askRemove = (conversation: ConversationDto) =>
    confirm({
      title: '删除这个对话？',
      message: `「${conversation.title}」的全部消息会被删除，不能恢复。已经创建的任务、审批等不受影响。`,
      confirmText: '删除',
      destructive: true,
      onConfirm: () => remove.mutate(conversation.id),
    });

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '对话' }} />
      <FlatList
        data={conversations.data ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        refreshControl={<RefreshControl refreshing={conversations.isRefetching} onRefresh={() => void conversations.refetch()} />}
        ListHeaderComponent={<Button title='新对话' onPress={() => router.push('/chat/new')} style={styles.newButton} />}
        ListEmptyComponent={conversations.isPending ? null : <Empty title='和你的 AI 团队对话' hint='在对话里直接创建任务、指派智能体、查看进展。' />}
        ListFooterComponent={conversations.data?.length ? <Muted style={styles.footer}>长按对话可以删除</Muted> : null}
        renderItem={({ item }) => (
          <Pressable
            onPress={() => router.push({ pathname: '/chat/[id]', params: { id: item.id } })}
            onLongPress={() => askRemove(item)}
            accessibilityRole='button'
            accessibilityHint='长按删除这个对话'
            style={({ pressed }) => [styles.row, { borderColor: p.border }, pressed && { opacity: 0.6 }]}
          >
            <Text style={{ color: p.text, fontSize: 15 }} numberOfLines={1}>
              {item.title}
            </Text>
            <Muted>{formatTime(item.lastMessageAt)}</Muted>
          </Pressable>
        )}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 8 },
  newButton: { marginBottom: 8 },
  row: { paddingVertical: 12, borderBottomWidth: StyleSheet.hairlineWidth, gap: 4 },
  footer: { textAlign: 'center', marginTop: 12 },
});
