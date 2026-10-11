'use client';

import { useDeferredValue, useMemo, useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import type { WorkItemDto } from '@ainative/api-client';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { api } from '@/lib/api';
import { boardColumns, priority, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

export default function TasksPage() {
  const { can } = useSession();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState('');
  const term = useDeferredValue(search.trim());
  const tasks = useQuery({
    queryKey: queryKeys.workItems({ search: term }),
    queryFn: ({ signal }) => api.workItems.list(undefined, undefined, undefined, term || undefined, false, 1, 200, signal),
  });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const agentNames = useMemo(() => new Map(agents.data?.map((a) => [a.id, a.name])), [agents.data]);
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
