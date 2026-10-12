'use client';

import { useDeferredValue, useMemo, useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import type { WorkItemDto } from '@ainative/api-client';
import { PageHeader } from '@/components/app/page-header';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { api } from '@/lib/api';
import { boardColumns, formatDate, isPastDate, priority, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

const ALL_PROJECTS = '__all__';

export default function TasksPage() {
  const { can } = useSession();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState('');
  const [projectId, setProjectId] = useState(ALL_PROJECTS);
  const term = useDeferredValue(search.trim());
  const project = projectId === ALL_PROJECTS ? undefined : projectId;
  const tasks = useQuery({
    queryKey: queryKeys.workItems({ search: term, projectId: project }),
    queryFn: ({ signal }) => api.workItems.list(undefined, undefined, undefined, term || undefined, false, 1, 200, project, undefined, signal),
  });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const projects = useQuery({
    queryKey: queryKeys.projects({ open: true, picker: true }),
    queryFn: ({ signal }) => api.projects.list(undefined, undefined, true, 1, 200, signal),
    enabled: can('projects.view'),
  });
  const agentNames = useMemo(() => new Map(agents.data?.map((a) => [a.id, a.name])), [agents.data]);
  const projectNames = useMemo(() => new Map(projects.data?.items.map((p) => [p.id, p.name])), [projects.data]);
  const byStatus = useMemo(() => {
    const groups = new Map<string, WorkItemDto[]>();
    for (const item of tasks.data?.items ?? []) {
      groups.set(item.status, [...(groups.get(item.status) ?? []), item]);
    }

    return groups;
  }, [tasks.data]);

  return (
    <div className='flex min-h-0 flex-1 flex-col gap-6 p-6'>
      <PageHeader
        title='任务'
        description='人和智能体共用的任务看板。指派给智能体的任务会自动被领取和推进。'
        actions={
          <>
            <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder='搜索任务' className='w-48' />
            {projects.data?.items.length ? (
              <Select value={projectId} onValueChange={setProjectId}>
                <SelectTrigger className='w-40'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL_PROJECTS}>全部项目</SelectItem>
                  {projects.data.items.map((p) => (
                    <SelectItem key={p.id} value={p.id}>
                      {p.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : null}
            {can('tasks.manage') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建任务
              </Button>
            ) : null}
          </>
        }
      />
      {tasks.isPending ? (
        <Skeleton className='h-64 w-full' />
      ) : tasks.isError && !tasks.data ? (
        <ErrorState query={tasks} />
      ) : (
        <div className='flex min-h-0 flex-1 gap-4 overflow-x-auto pb-2'>
          {boardColumns.map((status) => (
            <div key={status} className='bg-muted/40 flex w-72 shrink-0 flex-col rounded-xl p-2'>
              <div className='flex items-center justify-between px-2 py-1.5 text-sm font-medium'>
                {workItemStatus[status].label}
                <span className='text-muted-foreground'>{byStatus.get(status)?.length ?? 0}</span>
              </div>
              <div className='flex flex-col gap-2 overflow-y-auto'>
                {byStatus.get(status)?.map((task) => (
                  <Link
                    key={task.id}
                    href={`/tasks/view?id=${task.id}`}
                    className='bg-card hover:border-foreground/30 rounded-lg border p-3 text-sm shadow-xs transition-colors'
                  >
                    <div className='text-muted-foreground mb-1 flex items-center justify-between font-mono text-xs'>
                      {task.key}
                      {task.checkoutRunId ? <Badge variant='default'>执行中</Badge> : null}
                    </div>
                    <p className='line-clamp-2 font-medium'>{task.title}</p>
                    {task.projectId && projectNames.has(task.projectId) ? (
                      <p className='text-muted-foreground mt-1 flex items-center gap-1 truncate text-xs'>
                        <Icons.project className='size-3' />
                        {projectNames.get(task.projectId)}
                      </p>
                    ) : null}
                    {task.dueDate ? (
                      <p
                        className={cn(
                          'mt-1 flex items-center gap-1 text-xs',
                          task.status !== 'done' && task.status !== 'cancelled' && isPastDate(task.dueDate) ? 'text-warning' : 'text-muted-foreground',
                        )}
                      >
                        <Icons.calendar className='size-3' />
                        {formatDate(task.dueDate)} 截止
                      </p>
                    ) : null}
                    <div className='text-muted-foreground mt-2 flex items-center justify-between text-xs'>
                      <span>{task.assigneeAgentId ? agentNames.get(task.assigneeAgentId) ?? '智能体' : '未指派'}</span>
                      <span>优先级 {priority[task.priority]}</span>
                    </div>
                  </Link>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}
      <TaskCreateDialog open={creating} onOpenChange={setCreating} />
    </div>
  );
}
