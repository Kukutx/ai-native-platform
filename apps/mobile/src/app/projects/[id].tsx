import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan, type WorkItemDto } from '@ainative/api-client';
import { ProgressBar } from '@/components/progress';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatDate, formatProgress, projectStatus, workItemStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ProjectDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['project', id], queryFn: ({ signal }) => getApi().projects.get(id, signal) });
  const tasks = useQuery({
    queryKey: ['work-items', { projectId: id }],
    queryFn: ({ signal }) => getApi().workItems.list(undefined, undefined, undefined, undefined, false, 1, 200, id, undefined, signal),
    enabled: sessionCan(session.data, 'tasks.view'),
  });
  const toggleMilestone = useMutation({
    mutationFn: ({ milestoneId, completed }: { milestoneId: string; completed: boolean }) =>
      completed ? getApi().projects.reopenMilestone(id, milestoneId) : getApi().projects.completeMilestone(id, milestoneId),
    onSettled: async () => {
      await queryClient.invalidateQueries({ queryKey: ['project', id] });
      await queryClient.invalidateQueries({ queryKey: ['projects'] });
    },
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { project, milestones, members } = detail.data;
  const manage = sessionCan(session.data, 'projects.manage') && project.status !== 'completed' && project.status !== 'cancelled';
  const byMilestone = new Map<string, WorkItemDto[]>();
  for (const task of tasks.data?.items ?? []) {
    byMilestone.set(task.milestoneId ?? '', [...(byMilestone.get(task.milestoneId ?? '') ?? []), task]);
  }

  const unplaced = byMilestone.get('') ?? [];

  return (
    <ScrollView
      style={{ backgroundColor: p.background }}
      contentContainerStyle={styles.container}
      refreshControl={
        <RefreshControl
          refreshing={detail.isRefetching}
          onRefresh={() => {
            void detail.refetch();
            void tasks.refetch();
          }}
        />
      }
    >
      <Stack.Screen options={{ title: project.name }} />
      <Text style={[styles.title, { color: p.text }]}>{project.name}</Text>
      <Muted>
        {projectStatus[project.status]}
        {project.isOverdue ? ' · 已逾期' : ''}
        {project.targetDate ? ` · 目标 ${formatDate(project.targetDate)}` : ''}
        {project.lead ? ` · 负责人 ${project.lead.name ?? '—'}${project.lead.kind === 'agent' ? '（智能体）' : ''}` : ''}
      </Muted>
      <Card>
        <ProgressBar percent={project.progress.percent} />
        <Muted>{formatProgress(project.progress)}</Muted>
      </Card>
      {project.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{project.description}</Text> : null}
      {milestones.map((m) => (
        <Card key={m.id}>
          <View style={styles.row}>
            <Text style={[styles.section, { color: p.text, flex: 1 }]}>{m.name}</Text>
            <Text style={{ color: m.isOverdue ? p.warning : p.muted, fontSize: 13 }}>
              {m.completedAt ? '已完成' : m.isOverdue ? '已逾期' : m.dueDate ? `${formatDate(m.dueDate)} 截止` : ''}
            </Text>
          </View>
          <ProgressBar percent={m.progress.percent} />
          <Muted>{formatProgress(m.progress)}</Muted>
          <TaskRows items={byMilestone.get(m.id) ?? []} />
          {manage ? (
            <Button
              title={m.completedAt ? '重新打开' : '标记完成'}
              variant='outline'
              loading={toggleMilestone.isPending && toggleMilestone.variables?.milestoneId === m.id}
              onPress={() => toggleMilestone.mutate({ milestoneId: m.id, completed: !!m.completedAt })}
            />
          ) : null}
        </Card>
      ))}
      {unplaced.length || !milestones.length ? (
        <Card>
          <Text style={[styles.section, { color: p.text }]}>{milestones.length ? '未归入里程碑' : '任务'}</Text>
          {unplaced.length ? <TaskRows items={unplaced} /> : <Muted>项目里还没有任务。</Muted>}
        </Card>
      ) : null}
      {members.length ? (
        <Card>
          <Text style={[styles.section, { color: p.text }]}>成员</Text>
          {members.map((m) => (
            <Muted key={m.id}>
              {m.member.name ?? '—'}
              {m.member.kind === 'agent' ? '（智能体）' : ''}
              {project.lead?.kind === m.member.kind && project.lead?.id === m.member.id ? ' · 负责人' : ''}
              {m.responsibility ? ` · ${m.responsibility}` : ''}
              {m.openTasks ? ` · ${m.openTasks} 个未完成任务` : ''}
            </Muted>
          ))}
        </Card>
      ) : null}
    </ScrollView>
  );
}

function TaskRows({ items }: { items: WorkItemDto[] }) {
  const p = usePalette();
  return items.map((task) => (
    <Pressable key={task.id} onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: task.id } })} style={styles.taskRow}>
      <Text style={{ color: p.text, flex: 1 }} numberOfLines={1}>
        {task.key} {task.title}
      </Text>
      <Muted>{workItemStatus[task.status]}</Muted>
    </Pressable>
  ));
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  section: { fontSize: 16, fontWeight: '600' },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  taskRow: { flexDirection: 'row', alignItems: 'center', gap: 8, paddingVertical: 6 },
});
