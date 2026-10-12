'use client';

import { Suspense, useMemo, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { Pager } from '@/components/app/pager';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { TaskBoard } from '@/features/tasks/task-board';
import { TaskCreateDialog } from '@/features/tasks/task-create-dialog';
import { clientFilter, parseAssignee, priorityFilter, TaskFilterBar, taskFilterDefaults } from '@/features/tasks/task-filters';
import { TaskList } from '@/features/tasks/task-list';
import { useAssigneeNames, useChangeTaskStatus } from '@/features/tasks/task-update';
import { useUrlState } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/** 看板一次载入的任务数（后端单页上限）；列表视图在后端能筛选时按页读取。 */
const BOARD_LIMIT = 200;
const PAGE_SIZE = 50;

export default function TasksPage() {
  return (
    <Suspense>
      <Tasks />
    </Suspense>
  );
}

function Tasks() {
  const { can, session } = useSession();
  const [creating, setCreating] = useState(false);
  const [filters, setFilters] = useUrlState(taskFilterDefaults);
  const view = filters.view === 'list' ? 'list' : 'board';
  const assignee = parseAssignee(filters.assignee, session?.user.id);
  const priority = priorityFilter(filters.priority);
  const projectId = filters.project || undefined;
  const search = filters.q || undefined;
  const agentId = assignee?.agentId;
  const userId = assignee?.userId;
  const local = clientFilter({ assignee });
  // 列表视图在条件都能交给后端时按页读取；否则和看板一样载入前 200 个，在浏览器里筛选、分页。
  const paged = view === 'list' && !local;
  const page = Math.max(1, filters.page);
  const server = { search, projectId, agentId, userId, priority, ...(paged ? { page, pageSize: PAGE_SIZE } : { pageSize: BOARD_LIMIT }) };
  const listKey = queryKeys.workItems(server);
  const tasks = useQuery({
    queryKey: listKey,
    queryFn: ({ signal }) =>
      api.workItems.list(
        undefined,
        agentId,
        undefined,
        search,
        false,
        paged ? page : 1,
        paged ? PAGE_SIZE : BOARD_LIMIT,
        projectId,
        undefined,
        priority,
        userId,
        signal,
      ),
    placeholderData: keepPreviousData,
  });
  const names = useAssigneeNames();
  const projects = useQuery({
    queryKey: queryKeys.projects({ open: true, picker: true }),
    queryFn: ({ signal }) => api.projects.list(undefined, undefined, true, 1, 200, signal),
    enabled: can('projects.view'),
  });
  const projectNames = useMemo(() => new Map(projects.data?.items.map((p) => [p.id, p.name])), [projects.data]);
  const changeStatus = useChangeTaskStatus(listKey);
  const matched = (local ? tasks.data?.items.filter(local) : tasks.data?.items) ?? [];
  const total = paged ? (tasks.data?.total ?? 0) : matched.length;
  const shown = view === 'list' && !paged ? matched.slice((page - 1) * PAGE_SIZE, page * PAGE_SIZE) : matched;
  // 只载入了一部分时提示：看板和“未指派”筛选都只覆盖前 200 个（按优先级、编号倒序）。
  const truncated = !paged && tasks.data ? tasks.data.total > tasks.data.items.length : false;
  const filtered = !!(filters.q || filters.assignee || filters.project || filters.priority);
  const projectName = (id: string) => projectNames.get(id);

  return (
    <div className='flex min-h-0 flex-1 flex-col gap-4 p-6'>
      <PageHeader
        title='任务'
        description='人和智能体共用的任务看板。指派给智能体的任务会自动被领取和推进。'
        actions={
          <>
            <Tabs value={view} onValueChange={(next) => setFilters({ view: next, page: 1 })}>
              <TabsList aria-label='视图'>
                <TabsTrigger value='board'>
                  <Icons.board />
                  看板
                </TabsTrigger>
                <TabsTrigger value='list'>
                  <Icons.list />
                  列表
                </TabsTrigger>
              </TabsList>
            </Tabs>
            {can('tasks.manage') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建任务
              </Button>
            ) : null}
          </>
        }
      />
      <TaskFilterBar filters={filters} onChange={setFilters} members={names.members} agents={names.agents} />
      {truncated ? (
        <p className='text-muted-foreground text-xs'>
          只载入了优先级最高的 {tasks.data?.items.length} 个任务（共 {tasks.data?.total} 个），可以用关键词、项目、负责人或优先级缩小范围。
        </p>
      ) : null}
      {tasks.isPending ? (
        <Skeleton className='h-64 w-full' />
      ) : tasks.isError && !tasks.data ? (
        <ErrorState query={tasks} />
      ) : view === 'board' ? (
        <TaskBoard
          tasks={shown}
          movable={can('tasks.manage')}
          assigneeName={names.name}
          projectName={projectName}
          onMove={(task, status) => changeStatus.mutate({ task, status })}
        />
      ) : shown.length ? (
        <Card>
          <CardContent>
            <TaskList tasks={shown} assigneeName={names.name} projectName={projectName} />
            <Pager page={page} pageSize={PAGE_SIZE} total={total} onChange={(next) => setFilters({ page: next })} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState
          title={filtered ? '没有符合条件的任务' : '还没有任务'}
          description={filtered ? '换个条件试试，或者清除筛选。' : '新建一个任务，或者在对话里让智能体帮你建。'}
        />
      )}
      <TaskCreateDialog open={creating} onOpenChange={setCreating} />
    </div>
  );
}
