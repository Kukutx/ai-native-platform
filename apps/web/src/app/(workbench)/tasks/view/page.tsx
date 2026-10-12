'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { UpdateWorkItemRequest, WorkItemDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { ProjectPlacementFields } from '@/features/tasks/project-placement-fields';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { api } from '@/lib/api';
import { formatTime, priority, workItemStatus } from '@/lib/labels';
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
        dueDate: item.dueDate,
        // 基于这份数据修改：期间被别人或智能体改过时后端返回 409，而不是用旧值覆盖。
        expectedUpdatedAt: item.updatedAt,
        ...patch,
      });
    },
    // 失败（包括冲突）也刷新，界面回到最新内容。
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
    return <Skeleton className='m-6 h-40' />;
  }

  const { item, comments, subtasks } = detail.data;
  const editable = can('tasks.manage');

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
            <CardContent>
              {subtasks.length ? <SubtaskList items={subtasks} /> : <p className='text-muted-foreground text-sm'>没有子任务。</p>}
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
                  <Textarea value={comment} onChange={(e) => setComment(e.target.value)} placeholder='留言会唤醒负责的智能体' rows={3} />
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
            <Field label='状态'>
              <Select value={item.status} disabled={!editable} onValueChange={(v) => update.mutate({ status: v as WorkItemDto['status'] })}>
                <SelectTrigger className='w-full'>
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
            <Field label='优先级'>
              <Select value={item.priority} disabled={!editable} onValueChange={(v) => update.mutate({ priority: v as WorkItemDto['priority'] })}>
                <SelectTrigger className='w-full'>
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
            <Field label='负责的智能体'>
              <Select
                value={item.assigneeAgentId ?? UNASSIGNED}
                disabled={!editable}
                onValueChange={(v) => update.mutate({ assigneeAgentId: v === UNASSIGNED ? undefined : v })}
              >
                <SelectTrigger className='w-full'>
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
            <Field label='截止日期'>
              <Input
                type='date'
                key={item.dueDate ?? 'none'}
                defaultValue={item.dueDate ?? ''}
                disabled={!editable}
                onBlur={(e) => (e.target.value || undefined) !== (item.dueDate ?? undefined) && update.mutate({ dueDate: e.target.value || undefined })}
              />
            </Field>
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

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className='grid gap-2'>
      <Label>{label}</Label>
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
