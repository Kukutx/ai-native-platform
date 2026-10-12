'use client';

import Link from 'next/link';
import type { WorkItemDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatDate, formatTime, isPastDate, priority, workItemStatus } from '@/lib/labels';
import { cn } from '@/lib/utils';
import { isClosed } from './task-update';

/** 任务列表视图：一行一个任务，包括已取消的。 */
export function TaskList({
  tasks,
  assigneeName,
  projectName,
}: {
  tasks: WorkItemDto[];
  assigneeName: (task: WorkItemDto) => string | undefined;
  projectName: (projectId: string) => string | undefined;
}) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead className='w-20'>编号</TableHead>
          <TableHead>标题</TableHead>
          <TableHead>状态</TableHead>
          <TableHead>优先级</TableHead>
          <TableHead>负责人</TableHead>
          <TableHead>项目</TableHead>
          <TableHead>截止日期</TableHead>
          <TableHead>更新时间</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {tasks.map((task) => (
          <TableRow key={task.id}>
            <TableCell className='text-muted-foreground font-mono text-xs'>{task.key}</TableCell>
            <TableCell className='max-w-sm'>
              <Link href={`/tasks/view?id=${task.id}`} className='line-clamp-2 font-medium whitespace-normal hover:underline'>
                {task.title}
              </Link>
              {task.checkoutRunId ? <span className='text-muted-foreground text-xs'>智能体执行中</span> : null}
            </TableCell>
            <TableCell>
              <StatusBadge meta={workItemStatus[task.status]} />
            </TableCell>
            <TableCell className={cn(task.priority === 'urgent' && 'text-destructive', task.priority === 'high' && 'text-warning')}>{priority[task.priority]}</TableCell>
            <TableCell className='text-muted-foreground'>
              <span className='flex items-center gap-1'>
                {task.assigneeAgentId ? <Icons.agent className='size-3.5 shrink-0' /> : task.assigneeUserId ? <Icons.user className='size-3.5 shrink-0' /> : null}
                {assigneeName(task) ?? '未指派'}
              </span>
            </TableCell>
            <TableCell className='text-muted-foreground max-w-40 truncate'>{task.projectId ? (projectName(task.projectId) ?? '—') : '—'}</TableCell>
            <TableCell className={cn('tabular-nums', task.dueDate && !isClosed(task.status) && isPastDate(task.dueDate) ? 'text-warning' : 'text-muted-foreground')}>
              {formatDate(task.dueDate)}
            </TableCell>
            <TableCell className='text-muted-foreground text-xs'>{formatTime(task.updatedAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
