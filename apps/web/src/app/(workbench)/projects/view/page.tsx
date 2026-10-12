'use client';

import { Suspense, useMemo, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ProjectMilestoneDto, WorkItemDto } from '@ainative/api-client';
import { DetailState } from '@/components/app/query-state';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { StatCard } from '@/components/app/stat-card';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { InfoRow } from '@/components/app/info-row';
import { encodeActor } from '@/features/projects/actor-select';
import { GanttChart } from '@/features/projects/gantt-chart';
import { MemberDialog } from '@/features/projects/member-dialog';
import { MilestoneFormDialog } from '@/features/projects/milestone-form-dialog';
import { ProgressBar } from '@/features/projects/progress-bar';
import { ProjectFormDialog } from '@/features/projects/project-form-dialog';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { api } from '@/lib/api';
import { formatDate, formatTime, isPastDate, projectStatus, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

/** 项目里最多显示的任务数；更多的到任务页按项目查看。 */
const TASK_LIMIT = 200;

function ProjectDetail() {
  const params = useSearchParams();
  const id = params.get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const canSchedule = can('tasks.view');
  const view = canSchedule && params.get('view') === 'gantt' ? 'gantt' : 'overview';
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const [editing, setEditing] = useState(false);
  const [milestoneDialog, setMilestoneDialog] = useState<{ milestone: ProjectMilestoneDto | null } | null>(null);
  const [addingMember, setAddingMember] = useState(false);
  const [newTask, setNewTask] = useState<{ milestoneId?: string } | null>(null);
  const detail = useQuery({ queryKey: queryKeys.project(id), queryFn: ({ signal }) => api.projects.get(id, signal), enabled: !!id });
  const tasks = useQuery({
    queryKey: queryKeys.workItems({ projectId: id }),
    queryFn: ({ signal }) => api.workItems.list(undefined, undefined, undefined, undefined, false, 1, TASK_LIMIT, id, undefined, signal),
    enabled: !!id && can('tasks.view'),
  });
  const schedule = useQuery({
    queryKey: queryKeys.projectSchedule(id),
    queryFn: ({ signal }) => api.projects.schedule(id, signal),
    enabled: !!id && view === 'gantt',
  });
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.project(id) });
    await queryClient.invalidateQueries({ queryKey: ['projects'] });
  };
  const remove = useMutation({
    mutationFn: () => api.projects.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] });
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      router.replace('/projects');
    },
  });
  const milestoneAction = useMutation({
    mutationFn: async ({ milestone, action }: { milestone: ProjectMilestoneDto; action: 'complete' | 'reopen' | 'delete' }) => {
      if (action === 'delete') {
        await api.projects.deleteMilestone(id, milestone.id);
      } else {
        await (action === 'complete' ? api.projects.completeMilestone(id, milestone.id) : api.projects.reopenMilestone(id, milestone.id));
      }
    },
    onSettled: refresh,
  });
  const removeMember = useMutation({ mutationFn: (memberId: string) => api.projects.removeMember(id, memberId), onSettled: refresh });

  const groups = useMemo(() => {
    const byMilestone = new Map<string, WorkItemDto[]>();
    for (const task of tasks.data?.items ?? []) {
      const key = task.milestoneId ?? '';
      byMilestone.set(key, [...(byMilestone.get(key) ?? []), task]);
    }

    return byMilestone;
  }, [tasks.data]);

  if (!detail.data) {
    return <DetailState id={id} query={detail} subject='项目' back={{ href: '/projects', label: '项目' }} />;
  }

  const { project, milestones, members } = detail.data;
  const manage = can('projects.manage');
  const addTasks = can('tasks.manage') && project.status !== 'completed' && project.status !== 'cancelled';
  const editMilestones = manage && project.status !== 'completed' && project.status !== 'cancelled';
  const unplaced = groups.get('') ?? [];
  const progress = project.progress;

  return (
    <div className='space-y-6 p-6'>
      <Link href='/projects' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        项目
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <h1 className='flex items-center gap-2 text-2xl font-semibold tracking-tight'>
            <Icons.project className='text-muted-foreground size-6' />
            {project.name}
          </h1>
          <div className='flex flex-wrap items-center gap-2 text-sm'>
            <StatusBadge meta={projectStatus[project.status]} />
            {project.isOverdue ? <Badge variant='destructive'>已逾期</Badge> : null}
            {project.targetDate ? <span className='text-muted-foreground'>目标 {formatDate(project.targetDate)}</span> : null}
          </div>
        </div>
        <div className='flex gap-2'>
          {addTasks ? (
            <Button onClick={() => setNewTask({})}>
              <Icons.add />
              新建任务
            </Button>
          ) : null}
          {manage ? (
            <>
              <Button variant='outline' onClick={() => setEditing(true)}>
                <Icons.edit />
                编辑
              </Button>
              <Button
                variant='ghost'
                disabled={remove.isPending}
                onClick={() =>
                  void confirm({ title: `删除项目「${project.name}」？`, description: '里程碑和成员会一并删除，项目里的任务保留下来。', confirmLabel: '删除' }).then(
                    (ok) => ok && remove.mutate(),
                  )
                }
              >
                <Icons.trash />
                删除
              </Button>
            </>
          ) : null}
        </div>
      </div>
      <div className='grid gap-4 sm:grid-cols-2 lg:grid-cols-4'>
        <StatCard label='完成进度' value={`${progress.percent}%`} detail={`${progress.done}/${progress.total} 个任务已完成`} />
        <StatCard label='进行中' value={String(progress.inProgress)} detail='含待验收' />
        <StatCard label='受阻' value={String(progress.blocked)} tone={progress.blocked ? 'warning' : undefined} />
        <StatCard label='逾期' value={String(progress.overdue)} detail='截止日期已过、仍未完成' tone={progress.overdue ? 'warning' : undefined} />
      </div>
      <Tabs
        value={view}
        onValueChange={(v) => router.replace(v === 'gantt' ? `/projects/view?id=${id}&view=gantt` : `/projects/view?id=${id}`, { scroll: false })}
      >
        <TabsList>
          <TabsTrigger value='overview'>概览</TabsTrigger>
          {canSchedule ? <TabsTrigger value='gantt'>甘特图</TabsTrigger> : null}
        </TabsList>
        <TabsContent value='overview' className='mt-4'>
          <div className='grid gap-6 lg:grid-cols-3'>
            <div className='space-y-6 lg:col-span-2'>
              <div className='flex items-center justify-between'>
                <h2 className='text-lg font-semibold'>里程碑</h2>
                {editMilestones ? (
                  <Button size='sm' variant='outline' onClick={() => setMilestoneDialog({ milestone: null })}>
                    <Icons.add />
                    添加里程碑
                  </Button>
                ) : null}
              </div>
              {milestones.length === 0 ? <p className='text-muted-foreground text-sm'>还没有里程碑。把项目拆成几个阶段性目标，进度会更清楚。</p> : null}
              {milestones.map((m) => (
                <Card key={m.id} className={cn(m.completedAt && 'opacity-80')}>
                  <CardHeader className='space-y-3'>
                    <div className='flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between'>
                      <div className='min-w-0 space-y-1'>
                        <CardTitle className='flex items-center gap-2'>
                          <Icons.milestone className='text-muted-foreground size-4' />
                          {m.name}
                          {m.completedAt ? <Badge variant='success'>已完成</Badge> : m.isOverdue ? <Badge variant='destructive'>已逾期</Badge> : null}
                        </CardTitle>
                        <p className='text-muted-foreground text-xs'>
                          {m.dueDate ? `${formatDate(m.dueDate)} 截止` : '没有截止日期'}
                          {m.completedAt ? ` · ${formatTime(m.completedAt)} 完成` : null}
                        </p>
                        {m.description ? <p className='text-muted-foreground text-sm whitespace-pre-wrap'>{m.description}</p> : null}
                      </div>
                      <div className='flex shrink-0 items-center gap-1'>
                        {addTasks && !m.completedAt ? (
                          <Button size='sm' variant='ghost' onClick={() => setNewTask({ milestoneId: m.id })}>
                            <Icons.add />
                            任务
                          </Button>
                        ) : null}
                        {editMilestones ? (
                          <>
                            <Button
                              size='sm'
                              variant='outline'
                              disabled={milestoneAction.isPending}
                              onClick={() => milestoneAction.mutate({ milestone: m, action: m.completedAt ? 'reopen' : 'complete' })}
                            >
                              {m.completedAt ? '重新打开' : '标记完成'}
                            </Button>
                            <DropdownMenu>
                              <DropdownMenuTrigger asChild>
                                <Button size='icon' variant='ghost' aria-label='更多操作'>
                                  <Icons.dots />
                                </Button>
                              </DropdownMenuTrigger>
                              <DropdownMenuContent align='end'>
                                <DropdownMenuItem onSelect={() => setMilestoneDialog({ milestone: m })}>编辑</DropdownMenuItem>
                                <DropdownMenuItem
                                  onSelect={() =>
                                    void confirm({ title: `删除里程碑「${m.name}」？`, description: '里面的任务会留在项目里。', confirmLabel: '删除' }).then(
                                      (ok) => ok && milestoneAction.mutate({ milestone: m, action: 'delete' }),
                                    )
                                  }
                                >
                                  删除
                                </DropdownMenuItem>
                              </DropdownMenuContent>
                            </DropdownMenu>
                          </>
                        ) : null}
                      </div>
                    </div>
                    {m.progress.total ? <ProgressBar progress={m.progress} /> : null}
                  </CardHeader>
                  <CardContent>
                    <TaskList items={groups.get(m.id) ?? []} empty='这个里程碑下还没有任务。' />
                  </CardContent>
                </Card>
              ))}
              {unplaced.length || milestones.length === 0 ? (
                <Card>
                  <CardHeader>
                    <CardTitle>{milestones.length ? '未归入里程碑的任务' : '任务'}</CardTitle>
                  </CardHeader>
                  <CardContent>
                    <TaskList items={unplaced} empty='项目里还没有任务。' />
                  </CardContent>
                </Card>
              ) : null}
              {(tasks.data?.total ?? 0) > TASK_LIMIT ? <p className='text-muted-foreground text-xs'>只显示了前 {TASK_LIMIT} 个任务。</p> : null}
            </div>
            <div className='space-y-6'>
              <Card>
                <CardHeader>
                  <CardTitle>资料</CardTitle>
                </CardHeader>
                <CardContent className='space-y-3 text-sm'>
                  <InfoRow icon={Icons.user} label='负责人'>
                    {project.lead ? (
                      <>
                        {project.lead.name}
                        {project.lead.kind === 'agent' ? <span className='text-muted-foreground ml-1 text-xs'>智能体</span> : null}
                      </>
                    ) : null}
                  </InfoRow>
                  <InfoRow icon={Icons.calendar} label='开始'>
                    {project.startDate ? formatDate(project.startDate) : null}
                  </InfoRow>
                  <InfoRow icon={Icons.calendar} label='目标'>
                    {project.targetDate ? formatDate(project.targetDate) : null}
                  </InfoRow>
                  {project.description ? <p className='text-muted-foreground border-t pt-3 whitespace-pre-wrap'>{project.description}</p> : null}
                  <p className='text-muted-foreground border-t pt-3 text-xs'>
                    {project.createdBy.name ?? '系统'} 创建于 {formatTime(project.createdAt)}
                    {project.closedAt ? `，${formatTime(project.closedAt)} 结束` : null}
                  </p>
                </CardContent>
              </Card>
              <Card>
                <CardHeader className='flex flex-row items-center justify-between'>
                  <CardTitle>成员 {members.length ? <span className='text-muted-foreground font-normal'>{members.length}</span> : null}</CardTitle>
                  {manage ? (
                    <Button size='sm' variant='outline' onClick={() => setAddingMember(true)}>
                      <Icons.add />
                      添加
                    </Button>
                  ) : null}
                </CardHeader>
                <CardContent>
                  {members.length ? (
                    <ul className='divide-y text-sm'>
                      {members.map((m) => {
                        const isLead = project.lead?.kind === m.member.kind && project.lead?.id === m.member.id;
                        return (
                          <li key={m.id} className='flex items-center gap-3 py-2'>
                            <div className='min-w-0 flex-1'>
                              <p className='flex items-center gap-1 font-medium'>
                                <span className='truncate'>{m.member.name ?? '—'}</span>
                                {m.member.kind === 'agent' ? <span className='text-muted-foreground text-xs font-normal'>智能体</span> : null}
                                {isLead ? (
                                  <Badge variant='secondary' className='shrink-0'>
                                    负责人
                                  </Badge>
                                ) : null}
                              </p>
                              <p className='text-muted-foreground text-xs'>
                                {[m.responsibility, m.openTasks ? `${m.openTasks} 个未完成任务` : null].filter(Boolean).join(' · ') || '—'}
                              </p>
                            </div>
                            {manage && !isLead ? (
                              <Button
                                size='icon'
                                variant='ghost'
                                aria-label='移出项目'
                                disabled={removeMember.isPending}
                                onClick={() =>
                                  void confirm({ title: `把 ${m.member.name ?? 'TA'} 移出项目？`, confirmLabel: '移出' }).then((ok) => ok && removeMember.mutate(m.id))
                                }
                              >
                                <Icons.close />
                              </Button>
                            ) : null}
                          </li>
                        );
                      })}
                    </ul>
                  ) : (
                    <p className='text-muted-foreground text-sm'>还没有成员。</p>
                  )}
                </CardContent>
              </Card>
            </div>
          </div>
        </TabsContent>
        {canSchedule ? (
          <TabsContent value='gantt' className='mt-4'>
            {schedule.data ? <GanttChart data={schedule.data} editable={can('tasks.manage')} /> : <Skeleton className='h-96' />}
          </TabsContent>
        ) : null}
      </Tabs>
      <ProjectFormDialog open={editing} project={project} onOpenChange={setEditing} />
      <MilestoneFormDialog
        open={!!milestoneDialog}
        projectId={project.id}
        milestone={milestoneDialog?.milestone}
        onOpenChange={(open) => !open && setMilestoneDialog(null)}
      />
      <MemberDialog
        open={addingMember}
        projectId={project.id}
        exclude={members.map((m) => encodeActor(m.member.kind === 'agent' ? { agentId: m.member.id } : { userId: m.member.id }))}
        onOpenChange={setAddingMember}
      />
      <TaskCreateDialog open={!!newTask} onOpenChange={(open) => !open && setNewTask(null)} projectId={project.id} milestoneId={newTask?.milestoneId} />
    </div>
  );
}

function TaskList({ items, empty }: { items: WorkItemDto[]; empty: string }) {
  if (!items.length) {
    return <p className='text-muted-foreground text-sm'>{empty}</p>;
  }

  return (
    <ul className='divide-y text-sm'>
      {items.map((task) => {
        const overdue = !!task.dueDate && task.status !== 'done' && task.status !== 'cancelled' && isPastDate(task.dueDate);
        return (
          <li key={task.id} className='flex items-center gap-3 py-2'>
            <span className='text-muted-foreground font-mono text-xs'>{task.key}</span>
            <Link href={`/tasks/view?id=${task.id}`} className='min-w-0 flex-1 truncate hover:underline'>
              {task.title}
            </Link>
            {task.dueDate ? (
              <span className={cn('text-xs tabular-nums', overdue ? 'text-warning' : 'text-muted-foreground')}>
                {formatDate(task.dueDate)}
              </span>
            ) : null}
            <StatusBadge meta={workItemStatus[task.status]} />
          </li>
        );
      })}
    </ul>
  );
}

export default function ProjectDetailPage() {
  return (
    <Suspense>
      <ProjectDetail />
    </Suspense>
  );
}
