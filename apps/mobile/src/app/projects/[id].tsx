import { Pressable, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan, type ProjectScheduleDto, type WorkItemDto } from '@ainative/api-client';
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
    queryFn: ({ signal }) => getApi().workItems.list(undefined, undefined, undefined, undefined, false, 1, 200, id, undefined, undefined, undefined, signal),
    enabled: sessionCan(session.data, 'tasks.view'),
  });
  const schedule = useQuery({
    queryKey: ['project', id, 'schedule'],
    queryFn: ({ signal }) => getApi().projects.schedule(id, signal),
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
            void schedule.refetch();
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
      {schedule.data ? <ScheduleCard data={schedule.data} /> : null}
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

/** 排期一览的最多行数；完整的甘特图在 Web 端。 */
const SCHEDULE_ROWS = 30;

function toDay(value: string) {
  const [year = 1970, month = 1, day = 1] = value.split('-').map(Number);
  return Date.UTC(year, month - 1, day) / 86_400_000;
}

/** 简化的甘特图：按整个排期范围等比例画出每个有日期任务的时间条，标出关键路径和与前置任务冲突的任务。 */
function ScheduleCard({ data }: { data: ProjectScheduleDto }) {
  const p = usePalette();
  if (!data.start || !data.end) {
    return null;
  }

  const start = toDay(data.start);
  const total = toDay(data.end) - start + 1;
  const conflicted = new Set(data.links.filter((l) => l.conflict).map((l) => l.toId));
  const dated = data.tasks.filter((t) => t.startDate || t.dueDate);
  return (
    <Card>
      <Text style={[styles.section, { color: p.text }]}>排期</Text>
      <Muted>
        {formatDate(data.start)} – {formatDate(data.end)}
        {data.conflicts ? ` · ${data.conflicts} 处冲突` : ''}
        {data.unscheduled ? ` · ${data.unscheduled} 个未排期` : ''}
      </Muted>
      {dated.slice(0, SCHEDULE_ROWS).map((task) => {
        const from = toDay(task.startDate ?? task.dueDate!) - start;
        const to = toDay(task.dueDate ?? task.startDate!) - start + 1;
        const closed = task.status === 'done' || task.status === 'cancelled';
        const color = conflicted.has(task.id) ? p.danger : task.critical ? p.warning : closed ? p.success : p.primary;
        return (
          <Pressable key={task.id} onPress={() => router.push({ pathname: '/tasks/[id]', params: { id: task.id } })} style={styles.scheduleRow}>
            <Text style={{ color: p.text, fontSize: 13 }} numberOfLines={1}>
              {task.key} {task.title}
              {task.critical ? ' · 关键' : ''}
              {conflicted.has(task.id) ? ' · 早于前置任务' : ''}
            </Text>
            <View style={[styles.track, { backgroundColor: p.border }]}>
              <View
                style={{
                  position: 'absolute',
                  left: `${(from / total) * 100}%`,
                  width: `${Math.max(((to - from) / total) * 100, 1.5)}%`,
                  height: '100%',
                  borderRadius: 3,
                  backgroundColor: color,
                  opacity: closed ? 0.6 : 1,
                }}
              />
            </View>
          </Pressable>
        );
      })}
      {dated.length > SCHEDULE_ROWS ? <Muted>还有 {dated.length - SCHEDULE_ROWS} 个任务，完整的甘特图请在 Web 端查看。</Muted> : null}
      <Muted>橙色为关键路径（它晚一天项目就晚一天），红色为开始早于前置任务截止的任务。</Muted>
    </Card>
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
  scheduleRow: { gap: 4, paddingVertical: 4 },
  track: { height: 8, borderRadius: 3, overflow: 'hidden' },
});
