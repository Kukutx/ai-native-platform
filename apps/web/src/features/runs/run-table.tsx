import Link from 'next/link';
import type { RunDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatTime, formatUsd, runStatus } from '@/lib/labels';

export function RunTable({ runs, showAgent = true }: { runs: RunDto[]; showAgent?: boolean }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          {showAgent ? <TableHead>智能体</TableHead> : null}
          <TableHead>状态</TableHead>
          <TableHead>总结</TableHead>
          <TableHead className='text-right'>步数</TableHead>
          <TableHead className='text-right'>花费</TableHead>
          <TableHead>开始时间</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {runs.map((run) => (
          <TableRow key={run.id}>
            {showAgent ? <TableCell className='font-medium'>{run.agentName}</TableCell> : null}
            <TableCell>
              <StatusBadge meta={runStatus[run.status]} />
            </TableCell>
            <TableCell className='max-w-md'>
              <Link href={`/runs/view?id=${run.id}`} className='line-clamp-1 hover:underline'>
                {run.error ?? run.summary ?? '查看过程'}
              </Link>
            </TableCell>
            <TableCell className='text-right'>{run.steps}</TableCell>
            <TableCell className='text-right'>{formatUsd(run.costUsd)}</TableCell>
            <TableCell className='text-muted-foreground'>{formatTime(run.startedAt ?? run.createdAt)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
