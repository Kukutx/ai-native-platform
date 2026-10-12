import { useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { WorkItemLinkDto, WorkItemStatus } from '@ainative/api-client';
import { Button, Card, Chip, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatDate, formatTime, priority, workItemStatus } from '@/lib/labels';
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
        projectId: item.projectId,
        milestoneId: item.milestoneId,
        startDate: item.startDate,
        dueDate: item.dueDate,
        // 基于这份数据修改：期间被别人或智能体改过时后端返回 409，而不是用旧值覆盖。
        expectedUpdatedAt: item.updatedAt,
      });
    },
    // 失败（包括冲突）也刷新，界面回到最新内容。
    onSettled: refresh,
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

  const { item, comments, subtasks, dependencies, dependents } = detail.data;
  const closed = item.status === 'done' || item.status === 'cancelled';
  const waiting = closed ? [] : dependencies.filter((d) => d.status !== 'done' && d.status !== 'cancelled');

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: item.key }} />
      <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
        <Text style={[styles.title, { color: p.text }]}>{item.title}</Text>
        <Muted>
          {workItemStatus[item.status]} · 优先级 {priority[item.priority]}
          {item.checkoutRunId ? ' · 智能体执行中' : ''}
          {item.startDate ? ` · ${formatDate(item.startDate)} 开始` : ''}
          {item.dueDate ? ` · ${formatDate(item.dueDate)} 截止` : ''}
        </Muted>
        {waiting.length ? (
          <Card>
            <Text style={{ color: p.text }}>等待 {waiting.map((d) => d.key).join('、')} 关闭后才能开始，负责的智能体在此之前不会领取这个任务。</Text>
          </Card>
        ) : null}
        {item.projectId ? (
          <Text style={{ color: p.text, textDecorationLine: 'underline' }} onPress={() => router.push({ pathname: '/projects/[id]', params: { id: item.projectId! } })}>
            查看所属项目
          </Text>
        ) : null}
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
        {dependencies.length ? <LinkCard title='前置任务' items={dependencies} /> : null}
        {dependents.length ? <LinkCard title='后续任务' items={dependents} /> : null}
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

function LinkCard({ title, items }: { title: string; items: WorkItemLinkDto[] }) {
  const p = usePalette();
  return (
    <Card>
      <Text style={[styles.section, { color: p.text, marginTop: 0 }]}>{title}</Text>
      {items.map((d) => (
        <Text key={d.id} style={{ color: p.text }} onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: d.id } })}>
          {d.key} {d.title} · {workItemStatus[d.status]}
          {d.dueDate ? ` · ${formatDate(d.dueDate)} 截止` : ''}
        </Text>
      ))}
    </Card>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600', marginTop: 8 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
});
