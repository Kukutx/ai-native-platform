'use client';

import { useMemo, useState } from 'react';
import Link from 'next/link';
import type { WorkItemDto, WorkItemStatus } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { boardColumns, formatDate, isPastDate, priority, workItemStatus } from '@/lib/labels';
import { cn } from '@/lib/utils';
import { isClosed } from './task-update';

const DRAG_TYPE = 'application/x-work-item';
const allStatuses = Object.keys(workItemStatus) as WorkItemStatus[];

/**
 * 任务看板：按状态分列。有管理权限时可以把卡片拖到别的列改状态；
 * 每张卡片还有「移动到」菜单，键盘和读屏软件用户同样可以改状态。
 */
export function TaskBoard({
  tasks,
  movable,
  assigneeName,
  projectName,
  onMove,
}: {
  tasks: WorkItemDto[];
  movable: boolean;
  assigneeName: (task: WorkItemDto) => string | undefined;
  projectName: (projectId: string) => string | undefined;
  onMove: (task: WorkItemDto, status: WorkItemStatus) => void;
}) {
  const [dropTarget, setDropTarget] = useState<WorkItemStatus | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const byStatus = useMemo(() => {
    const groups = new Map<WorkItemStatus, WorkItemDto[]>();
    for (const task of tasks) {
      groups.set(task.status, [...(groups.get(task.status) ?? []), task]);
    }

    return groups;
  }, [tasks]);

  const move = (task: WorkItemDto, status: WorkItemStatus) => {
    if (task.status === status) {
      return;
    }

    onMove(task, status);
    setAnnouncement(`已把 ${task.key} 移到「${workItemStatus[status].label}」`);
  };

  return (
    <div className='flex min-h-0 flex-1 gap-4 overflow-x-auto pb-2'>
      {/* 拖放结果只有视觉变化，读屏软件通过这里得知。 */}
      <p className='sr-only' role='status' aria-live='polite'>
        {announcement}
      </p>
      {boardColumns.map((status) => {
        const column = byStatus.get(status) ?? [];
        return (
          <section
            key={status}
            aria-label={`${workItemStatus[status].label}，${column.length} 个任务`}
            className={cn(
              'bg-muted/40 flex w-72 shrink-0 flex-col rounded-xl p-2 transition-colors',
              dropTarget === status && 'bg-primary/10 ring-primary/40 ring-2',
            )}
            onDragOver={(event) => {
              if (movable && event.dataTransfer.types.includes(DRAG_TYPE)) {
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                setDropTarget(status);
              }
            }}
            onDragLeave={(event) => {
              if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
                setDropTarget(null);
              }
            }}
            onDrop={(event) => {
              event.preventDefault();
              setDropTarget(null);
              const task = tasks.find((t) => t.id === event.dataTransfer.getData(DRAG_TYPE));
              if (task) {
                move(task, status);
              }
            }}
          >
            <h2 className='flex items-center justify-between px-2 py-1.5 text-sm font-medium'>
              {workItemStatus[status].label}
              <span className='text-muted-foreground'>{column.length}</span>
            </h2>
            <div className='flex min-h-16 flex-col gap-2 overflow-y-auto'>
              {column.map((task) => (
                <TaskCard
                  key={task.id}
                  task={task}
                  movable={movable}
                  assignee={assigneeName(task)}
                  project={task.projectId ? projectName(task.projectId) : undefined}
                  onMove={(next) => move(task, next)}
                />
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

function TaskCard({
  task,
  movable,
  assignee,
  project,
  onMove,
}: {
  task: WorkItemDto;
  movable: boolean;
  assignee?: string;
  project?: string;
  onMove: (status: WorkItemStatus) => void;
}) {
  const [dragging, setDragging] = useState(false);

  return (
    <div
      draggable={movable}
      onDragStart={(event) => {
        event.dataTransfer.setData(DRAG_TYPE, task.id);
        event.dataTransfer.effectAllowed = 'move';
        setDragging(true);
      }}
      onDragEnd={() => setDragging(false)}
      className={cn(
        'bg-card hover:border-foreground/30 group relative rounded-lg border p-3 text-sm shadow-xs transition-colors',
        movable && 'cursor-grab active:cursor-grabbing',
        dragging && 'opacity-50',
      )}
    >
      {/* 链接本身不可拖动，拖动卡片任意位置都按卡片处理。 */}
      <Link href={`/tasks/view?id=${task.id}`} draggable={false} className='block'>
        <div className='text-muted-foreground mb-1 flex items-center gap-2 pr-6 font-mono text-xs'>
          {task.key}
          {task.checkoutRunId ? <Badge variant='default'>执行中</Badge> : null}
        </div>
        <p className='line-clamp-2 font-medium'>{task.title}</p>
        {project ? (
          <p className='text-muted-foreground mt-1 flex items-center gap-1 truncate text-xs'>
            <Icons.project className='size-3 shrink-0' />
            {project}
          </p>
        ) : null}
        {task.dueDate ? (
          <p className={cn('mt-1 flex items-center gap-1 text-xs', !isClosed(task.status) && isPastDate(task.dueDate) ? 'text-warning' : 'text-muted-foreground')}>
            <Icons.calendar className='size-3' />
            {formatDate(task.dueDate)} 截止
          </p>
        ) : null}
        <div className='text-muted-foreground mt-2 flex items-center justify-between gap-2 text-xs'>
          <span className='flex min-w-0 items-center gap-1'>
            {task.assigneeAgentId ? <Icons.agent className='size-3 shrink-0' /> : task.assigneeUserId ? <Icons.user className='size-3 shrink-0' /> : null}
            <span className='truncate'>{assignee ?? '未指派'}</span>
          </span>
          <span className={cn('shrink-0', task.priority === 'urgent' && 'text-destructive', task.priority === 'high' && 'text-warning')}>
            优先级 {priority[task.priority]}
          </span>
        </div>
      </Link>
      {movable ? (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              size='icon'
              variant='ghost'
              className='absolute top-2 right-2 size-6 opacity-100 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100 md:opacity-0'
              aria-label={`把 ${task.key} 移动到其他状态`}
            >
              <Icons.dots className='size-4' />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align='end'>
            <DropdownMenuLabel className='text-muted-foreground text-xs'>移动到</DropdownMenuLabel>
            <DropdownMenuSeparator />
            {allStatuses.map((status) => (
              <DropdownMenuItem key={status} disabled={status === task.status} onSelect={() => onMove(status)}>
                {workItemStatus[status].label}
                {status === task.status ? <Icons.check className='ml-auto size-4' /> : null}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : null}
    </div>
  );
}
