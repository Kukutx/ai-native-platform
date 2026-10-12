'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UpdateWorkItemRequest, WorkItemDto, WorkItemLinkDto } from '@ainative/api-client';
import { DetailState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { ProjectPlacementFields } from '@/features/tasks/project-placement-fields';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { TaskPicker } from '@/features/tasks/task-picker';
import { api } from '@/lib/api';
import { formatDate, formatTime, priority, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const UNASSIGNED = '__none__';

function TaskDetail() {
  const id = useSearchParams().get('id') ?? '';
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [comment, setComment] = useState('');
  const [creatingSubtask, setCreatingSubtask] = useState(false);
  const detail = useQuery({ queryKey: queryKeys.workItem(id), queryFn: ({ signal }) => api.workItems.get(id, signal), enabled: !!id });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: queryKeys.workItem(id) });
    await queryClient.invalidateQueries({ queryKey: ['work-items'] });
  };
  const update = useMutation({
    mutationFn: (patch: Partial<UpdateWorkItemRequest>) => {
      const item = detail.data!.item;
      return api.workItems.update(id, {
        title: item.title,
        description: item.description,
        status: item.status,
        priority: item.priority,
        assigneeAgentId: item.assigneeAgentId,
        assigneeUserId: item.assigneeUserId,
        projectId: item.projectId,
        milestoneId: item.milestoneId,
        startDate: item.startDate,
        dueDate: item.dueDate,
        // 基于这份数据修改：期间被别人或智能体改过时后端返回 409，而不是用旧值覆盖。
        expectedUpdatedAt: item.updatedAt,
        ...patch,
      });
    },
    // 失败（包括冲突）也刷新，界面回到最新内容。
    onSettled: refresh,
  });
  const dependency = useMutation({
    mutationFn: async ({ dependsOnId, remove }: { dependsOnId: string; remove?: boolean }) => {
      await (remove ? api.workItems.removeDependency(id, dependsOnId) : api.workItems.addDependency(id, { dependsOnId }));
    },
    onSettled: refresh,
  });
  const addComment = useMutation({
    mutationFn: () => api.workItems.addComment(id, { body: comment.trim() }),
    onSuccess: async () => {
      setComment('');
      await refresh();
    },
  });

  if (!detail.data) {
    return <DetailState id={id} query={detail} subject='任务' back={{ href: '/tasks', label: '任务' }} />;
  }

  const { item, comments, subtasks, dependencies, dependents } = detail.data;
  const editable = can('tasks.manage');
  const waiting = dependencies.filter((d) => d.status !== 'done' && d.status !== 'cancelled');

  return (
    <div className='space-y-6 p-6'>
      <Link href='/tasks' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        任务
      </Link>
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          <div>
            <p className='text-muted-foreground font-mono text-sm'>{item.key}</p>
            <h1 className='text-2xl font-semibold tracking-tight'>{item.title}</h1>
          </div>
          {waiting.length > 0 && item.status !== 'done' && item.status !== 'cancelled' ? (
            <p className='flex items-center gap-2 rounded-md border border-warning/40 bg-warning/10 px-3 py-2 text-sm'>
              <Icons.hourglass className='size-4 shrink-0 text-warning' />
              等待 {waiting.map((d) => d.key).join('、')} 关闭后才能开始，负责的智能体在此之前不会领取这个任务。
            </p>
          ) : null}
          <Card>
            <CardContent className='text-sm whitespace-pre-wrap'>{item.description || '（没有描述）'}</CardContent>
          </Card>
          <Card>
            <CardHeader className='flex flex-row items-center justify-between'>
              <CardTitle>子任务</CardTitle>
              {editable ? (
                <Button size='sm' variant='outline' onClick={() => setCreatingSubtask(true)}>
                  <Icons.add />
                  添加
                </Button>
              ) : null}
            </CardHeader>
            <CardContent>{subtasks.length ? <SubtaskList items={subtasks} /> : <p className='text-muted-foreground text-sm'>没有子任务。</p>}</CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>依赖</CardTitle>
            </CardHeader>
            <CardContent className='space-y-4 text-sm'>
              <div className='space-y-2'>
                <p className='text-muted-foreground text-xs'>前置任务：都关闭（完成或取消）后才能开始</p>
                {dependencies.length ? (
                  <LinkList
                    items={dependencies}
                    onRemove={editable ? (d) => dependency.mutate({ dependsOnId: d.id, remove: true }) : undefined}
                    disabled={dependency.isPending}
                  />
                ) : (
                  <p className='text-muted-foreground'>没有前置任务。</p>
                )}
                {editable ? (
                  <TaskPicker
                    exclude={[item.id, ...dependencies.map((d) => d.id), ...dependents.map((d) => d.id)]}
                    projectId={item.projectId}
                    placeholder={item.projectId ? '添加前置任务：搜索项目里的任务' : '添加前置任务：搜索任务'}
                    disabled={dependency.isPending}
                    onPick={(task) => dependency.mutate({ dependsOnId: task.id })}
                  />
                ) : null}
              </div>
              {dependents.length ? (
                <div className='space-y-2'>
                  <p className='text-muted-foreground text-xs'>后续任务：在等这个任务</p>
                  <LinkList items={dependents} />
                </div>
              ) : null}
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>讨论</CardTitle>
            </CardHeader>
            <CardContent className='space-y-4'>
              {comments.map((c) => (
                <div key={c.id} className='space-y-1'>
                  <div className='flex items-center gap-2 text-sm'>
                    <span className='font-medium'>{c.author.name ?? '系统'}</span>
                    {c.author.kind === 'agent' ? <span className='text-muted-foreground text-xs'>智能体</span> : null}
                    <span className='text-muted-foreground text-xs'>{formatTime(c.createdAt)}</span>
                    {c.runId ? (
                      <Link href={`/runs/view?id=${c.runId}`} className='text-muted-foreground text-xs underline'>
                        执行过程
                      </Link>
                    ) : null}
                  </div>
                  <p className='text-sm whitespace-pre-wrap'>{c.body}</p>
                </div>
              ))}
              {can('tasks.comment') ? (
                <form
                  className='space-y-2'
                  onSubmit={(event) => {
                    event.preventDefault();
                    if (comment.trim()) {
                      addComment.mutate();
                    }
                  }}
                >
                  <Textarea value={comment} onChange={(e) => setComment(e.target.value)} placeholder='留言会唤醒负责的智能体' rows={3} aria-label='留言' />
                  <Button type='submit' size='sm' disabled={addComment.isPending || !comment.trim()}>
                    发送
                  </Button>
                </form>
              ) : null}
            </CardContent>
          </Card>
        </div>
        <Card className='h-fit'>
          <CardHeader>
            <CardTitle>属性</CardTitle>
          </CardHeader>
          <CardContent className='space-y-4 text-sm'>
            <Field id='task-status' label='状态'>
              <Select value={item.status} disabled={!editable} onValueChange={(v) => update.mutate({ status: v as WorkItemDto['status'] })}>
                <SelectTrigger id='task-status' className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(workItemStatus).map(([key, meta]) => (
                    <SelectItem key={key} value={key}>
                      {meta.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field id='task-priority' label='优先级'>
              <Select value={item.priority} disabled={!editable} onValueChange={(v) => update.mutate({ priority: v as WorkItemDto['priority'] })}>
                <SelectTrigger id='task-priority' className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(priority).map(([key, label]) => (
                    <SelectItem key={key} value={key}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field id='task-assignee' label='负责的智能体'>
              <Select
                value={item.assigneeAgentId ?? UNASSIGNED}
                disabled={!editable}
                onValueChange={(v) => update.mutate({ assigneeAgentId: v === UNASSIGNED ? undefined : v })}
              >
                <SelectTrigger id='task-assignee' className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={UNASSIGNED}>未指派</SelectItem>
                  {agents.data
                    ?.filter((a) => a.status !== 'terminated')
                    .map((a) => (
                      <SelectItem key={a.id} value={a.id}>
                        {a.name}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </Field>
            <ProjectPlacementFields
              value={{ projectId: item.projectId, milestoneId: item.milestoneId }}
              disabled={!editable || update.isPending}
              onChange={(placement) => update.mutate({ projectId: placement.projectId, milestoneId: placement.milestoneId })}
            />
            {item.projectId ? (
              <Link href={`/projects/view?id=${item.projectId}`} className='text-muted-foreground -mt-2 inline-flex items-center gap-1 text-xs hover:underline'>
                <Icons.project className='size-3' />
                打开项目
              </Link>
            ) : null}
            <div className='grid grid-cols-2 gap-3'>
              <Field id='task-start' label='开始日期'>
                <Input
                  id='task-start'
                  type='date'
                  key={item.startDate ?? 'none'}
                  defaultValue={item.startDate ?? ''}
                  max={item.dueDate}
                  disabled={!editable}
                  onBlur={(e) => (e.target.value || undefined) !== (item.startDate ?? undefined) && update.mutate({ startDate: e.target.value || undefined })}
                />
              </Field>
              <Field id='task-due' label='截止日期'>
                <Input
                  id='task-due'
                  type='date'
                  key={item.dueDate ?? 'none'}
                  defaultValue={item.dueDate ?? ''}
                  min={item.startDate}
                  disabled={!editable}
                  onBlur={(e) => (e.target.value || undefined) !== (item.dueDate ?? undefined) && update.mutate({ dueDate: e.target.value || undefined })}
                />
              </Field>
            </div>
            <div className='text-muted-foreground space-y-1 pt-2'>
              <p>创建人：{item.createdBy.name ?? '系统'}</p>
              <p>创建于：{formatTime(item.createdAt)}</p>
              {item.completedAt ? <p>完成于：{formatTime(item.completedAt)}</p> : null}
              {item.checkoutRunId ? (
                <p>
                  正在执行：
                  <Link href={`/runs/view?id=${item.checkoutRunId}`} className='underline'>
                    查看实时过程
                  </Link>
                </p>
              ) : null}
            </div>
          </CardContent>
        </Card>
      </div>
      <TaskCreateDialog open={creatingSubtask} onOpenChange={setCreatingSubtask} parentId={item.id} />
    </div>
  );
}

function SubtaskList({ items }: { items: WorkItemDto[] }) {
  return (
    <ul className='divide-y text-sm'>
      {items.map((task) => (
        <li key={task.id} className='flex items-center gap-3 py-2'>
          <span className='text-muted-foreground font-mono'>{task.key}</span>
          <Link href={`/tasks/view?id=${task.id}`} className='flex-1 truncate hover:underline'>
            {task.title}
          </Link>
          <StatusBadge meta={workItemStatus[task.status]} />
        </li>
      ))}
    </ul>
  );
}

function LinkList({ items, onRemove, disabled }: { items: WorkItemLinkDto[]; onRemove?: (item: WorkItemLinkDto) => void; disabled?: boolean }) {
  return (
    <ul className='divide-y'>
      {items.map((task) => (
        <li key={task.id} className='flex items-center gap-3 py-2'>
          <span className='text-muted-foreground font-mono text-xs'>{task.key}</span>
          <Link href={`/tasks/view?id=${task.id}`} className='min-w-0 flex-1 truncate hover:underline'>
            {task.title}
          </Link>
          {task.dueDate ? <span className='text-muted-foreground text-xs tabular-nums'>{formatDate(task.dueDate)}</span> : null}
          <StatusBadge meta={workItemStatus[task.status]} />
          {onRemove ? (
            <Button size='icon' variant='ghost' className='size-7' aria-label={`去掉前置任务 ${task.key}`} disabled={disabled} onClick={() => onRemove(task)}>
              <Icons.close className='size-4' />
            </Button>
          ) : null}
        </li>
      ))}
    </ul>
  );
}

/** 属性栏的一项：htmlFor 指向控件的 id，读屏软件和点击标签都能定位到控件。 */
function Field({ id, label, children }: { id: string; label: string; children: React.ReactNode }) {
  return (
    <div className='grid gap-2'>
      <Label htmlFor={id}>{label}</Label>
      {children}
    </div>
  );
}

export default function TaskDetailPage() {
  return (
    <Suspense>
      <TaskDetail />
    </Suspense>
  );
}
