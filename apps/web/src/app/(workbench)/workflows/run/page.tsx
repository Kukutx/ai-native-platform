'use client';

import { Suspense } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { ValueRow } from '@/components/app/info-row';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { RunSteps } from '@/features/workflows/run-steps';
import { api } from '@/lib/api';
import { formatTime, workflowRunStatus, workflowTriggerKind } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const active = new Set(['queued', 'running', 'waiting']);

function WorkflowRunDetail() {
  const id = useSearchParams().get('id') ?? '';
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  // 运行进度通过实时通道推送（workflow_run 变化），这里不需要轮询。
  const detail = useQuery({ queryKey: queryKeys.workflowRun(id), queryFn: ({ signal }) => api.workflows.run(id, signal), enabled: !!id });
  const cancel = useMutation({
    mutationFn: () => api.workflows.cancel(id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.workflowRun(id) }),
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const run = detail.data;
  const { summary } = run;

  return (
    <div className='space-y-6 p-6'>
      <Link href={`/workflows/view?id=${summary.workflowId}`} className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        {summary.workflowName}
      </Link>
      <div className='flex flex-wrap items-center justify-between gap-3'>
        <div className='flex flex-wrap items-center gap-3'>
          <h1 className='text-2xl font-semibold tracking-tight'>
            {summary.workflowName}
            <span className='text-muted-foreground'> 的一次运行</span>
          </h1>
          <StatusBadge meta={workflowRunStatus[summary.status]} />
        </div>
        {active.has(summary.status) && can('workflows.run') ? (
          <Button variant='outline' disabled={cancel.isPending} onClick={() =>
              void confirm({ title: '取消这次运行？', description: '已完成的步骤不会回滚。', confirmLabel: '取消运行', cancelLabel: '继续运行' }).then(
                (ok) => ok && cancel.mutate(),
              )
            }>
            取消运行
          </Button>
        ) : null}
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <Card className='lg:col-span-2'>
          <CardHeader>
            <CardTitle>步骤</CardTitle>
          </CardHeader>
          <CardContent>
            <RunSteps run={run} />
          </CardContent>
        </Card>
        <div className='space-y-6'>
          <Card>
            <CardHeader>
              <CardTitle>概览</CardTitle>
            </CardHeader>
            <CardContent className='space-y-2 text-sm'>
              <ValueRow label='触发' value={summary.eventType ? `${workflowTriggerKind[summary.triggerKind]} · ${summary.eventType}` : workflowTriggerKind[summary.triggerKind]} />
              <ValueRow label='触发者' value={summary.triggeredBy.name ?? '—'} />
              <ValueRow label='运行身份' value={`${run.owner.name ?? '—'}${run.owner.kind === 'agent' ? '（智能体）' : ''}`} />
              <ValueRow label='工作流版本' value={run.workflowVersion} />
              <ValueRow label='进度' value={`${Math.min(summary.currentStep, summary.stepCount)} / ${summary.stepCount}`} />
              <ValueRow label='创建' value={formatTime(summary.createdAt)} />
              <ValueRow label='开始' value={formatTime(summary.startedAt)} />
              <ValueRow label='结束' value={formatTime(summary.finishedAt)} />
              {summary.error ? <p className='text-destructive pt-2 whitespace-pre-wrap'>{summary.error}</p> : null}
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>触发数据</CardTitle>
            </CardHeader>
            <CardContent className='grid gap-3'>
              <pre className='bg-muted max-h-72 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(run.trigger ?? {}, null, 2)}</pre>
              {run.input && Object.keys(run.input as object).length ? (
                <>
                  <p className='text-sm font-medium'>运行参数</p>
                  <pre className='bg-muted max-h-72 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(run.input, null, 2)}</pre>
                </>
              ) : null}
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}


export default function WorkflowRunPage() {
  return (
    <Suspense>
      <WorkflowRunDetail />
    </Suspense>
  );
}
