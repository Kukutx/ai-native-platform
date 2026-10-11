import Link from 'next/link';
import type { WorkflowRunSummaryDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatTime, workflowRunStatus, workflowTriggerKind } from '@/lib/labels';

export function WorkflowRunTable({ runs, showWorkflow = true }: { runs: WorkflowRunSummaryDto[]; showWorkflow?: boolean }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          {showWorkflow ? <TableHead>工作流</TableHead> : null}
          <TableHead>状态</TableHead>
          <TableHead>进度</TableHead>
          <TableHead>触发</TableHead>
          <TableHead>说明</TableHead>
          <TableHead>开始时间</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {runs.map((run) => (
          <TableRow key={run.id}>
            {showWorkflow ? (
              <TableCell className='font-medium'>
                <Link href={`/workflows/view?id=${run.workflowId}`} className='hover:underline'>
                  {run.workflowName}
                </Link>
              </TableCell>
            ) : null}
            <TableCell>
              <StatusBadge meta={workflowRunStatus[run.status]} />
            </TableCell>
            <TableCell className='text-muted-foreground'>
              {Math.min(run.currentStep, run.stepCount)} / {run.stepCount}
            </TableCell>
            <TableCell className='text-muted-foreground'>
              {workflowTriggerKind[run.triggerKind]}
              {run.triggeredBy.name ? ` · ${run.triggeredBy.name}` : ''}
            </TableCell>
            <TableCell className='max-w-sm'>
              <Link href={`/workflows/run?id=${run.id}`} className='line-clamp-1 hover:underline'>
                {run.error ?? '查看详情'}
              </Link>
            </TableCell>
            <TableCell className='text-muted-foreground'>{formatTime(run.startedAt ?? run.createdAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
