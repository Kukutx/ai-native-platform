import { useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { WorkItemStatus } from '@ainative/api-client';
import { Button, Card, Chip, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatTime, priority, workItemStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

const quickStatuses: WorkItemStatus[] = ['todo', 'in_progress', 'in_review', 'blocked', 'done'];

export default function TaskDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const [comment, setComment] = useState('');
  const detail = useQuery({
    queryKey: ['work-item', id],
    queryFn: ({ signal }) => getApi().workItems.get(id, signal),
    // 实时通道断开时的兜底：智能体执行期间定时刷新。
    refetchInterval: (query) => (query.state.data?.item.checkoutRunId ? 5000 : false),
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['work-item', id] });
    await queryClient.invalidateQueries({ queryKey: ['work-items'] });
  };
  const setStatus = useMutation({
    mutationFn: (status: WorkItemStatus) => {
      const item = detail.data!.item;
      return getApi().workItems.update(id, {
        title: item.title,
        description: item.description,
        status,
        priority: item.priority,
        assigneeAgentId: item.assigneeAgentId,
        assigneeUserId: item.assigneeUserId,
      });
    },
    onSuccess: refresh,
  });
  const addComment = useMutation({
    mutationFn: () => getApi().workItems.addComment(id, { body: comment.trim() }),
    onSuccess: async () => {
      setComment('');
      await refresh();
    },
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { item, comments, subtasks } = detail.data;

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: item.key }} />
      <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
        <Text style={[styles.title, { color: p.text }]}>{item.title}</Text>
        <Muted>
          {workItemStatus[item.status]} · 优先级 {priority[item.priority]}
          {item.checkoutRunId ? ' · 智能体执行中' : ''}
        </Muted>
        {item.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{item.description}</Text> : null}
        <View style={styles.chips}>
          {quickStatuses.map((status) => (
            <Chip key={status} label={workItemStatus[status]} active={item.status === status} onPress={() => setStatus.mutate(status)} />
          ))}
        </View>
        {subtasks.length ? (
          <Card>
            <Text style={[styles.section, { color: p.text }]}>子任务</Text>
            {subtasks.map((s) => (
              <Muted key={s.id}>
                {s.key} {s.title} · {workItemStatus[s.status]}
              </Muted>
            ))}
          </Card>
        ) : null}
        <Text style={[styles.section, { color: p.text }]}>讨论</Text>
        {comments.map((c) => (
          <Card key={c.id}>
            <Muted>
              {c.author.name ?? '系统'}
              {c.author.kind === 'agent' ? '（智能体）' : ''} · {formatTime(c.createdAt)}
            </Muted>
            <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{c.body}</Text>
          </Card>
        ))}
        <Field label='留言' value={comment} onChangeText={setComment} multiline placeholder='留言会唤醒负责的智能体' autoCapitalize='sentences' />
        <Button title='发送' onPress={() => addComment.mutate()} loading={addComment.isPending} disabled={!comment.trim()} />
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600', marginTop: 8 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
});
