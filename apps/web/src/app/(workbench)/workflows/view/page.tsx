'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { describeTrigger } from '@/features/workflows/describe';
import { RunWorkflowButton } from '@/features/workflows/run-workflow-dialog';
import { WorkflowRunTable } from '@/features/workflows/workflow-run-table';
import { api } from '@/lib/api';
import { formatTime, workflowStepKind, workflowTriggerKind } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 20;

function WorkflowDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const detail = useQuery({ queryKey: queryKeys.workflow(id), queryFn: ({ signal }) => api.workflows.get(id, signal), enabled: !!id });
  const catalog = useQuery({ queryKey: queryKeys.workflowCatalog, queryFn: ({ signal }) => api.workflows.catalog(signal) });
  const actions = useQuery({ queryKey: queryKeys.actions, queryFn: ({ signal }) => api.actions.list(signal) });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const runs = useQuery({
    queryKey: queryKeys.workflowRuns({ workflowId: id, page }),
    queryFn: ({ signal }) => api.workflows.runs(id, undefined, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
    enabled: !!id,
  });
  const setEnabled = useMutation({
    mutationFn: (enabled: boolean) => api.workflows.setEnabled(id, { enabled }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.workflows }),
  });
  const remove = useMutation({
    mutationFn: () => api.workflows.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.workflows });
      toast.success('已删除');
      router.push('/workflows');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const workflow = detail.data;
  const manage = can('workflows.manage');
  const pages = runs.data ? Math.max(1, Math.ceil(runs.data.total / PAGE_SIZE)) : 1;
  const stepDetail = (step: (typeof workflow.steps)[number]) => {
    switch (step.kind ?? 'action') {
      case 'agent_task': {
        const agent = agents.data?.find((a) => a.id === step.agentId);
        return `${agent?.name ?? '智能体'}：${step.title ?? ''}${step.waitForCompletion ? '（等待完成）' : ''}`;
      }
      case 'llm':
        return step.prompt ?? '';
      case 'delay':
        return `${step.delaySeconds ?? 0} 秒`;
      default:
        return actions.data?.find((a) => a.key === step.actionKey)?.title ?? step.actionKey ?? '';
    }
  };

  return (
    <div className='space-y-6 p-6'>
      <Link href='/workflows' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        自动化
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-1'>
          <div className='flex flex-wrap items-center gap-3'>
            <h1 className='text-2xl font-semibold tracking-tight'>{workflow.name}</h1>
            <Badge variant={workflow.isEnabled ? 'secondary' : 'outline'}>{workflow.isEnabled ? '已启用' : '已停用'}</Badge>
          </div>
          {workflow.description ? <p className='text-muted-foreground text-sm'>{workflow.description}</p> : null}
        </div>
        <div className='flex flex-wrap gap-2'>
          {can('workflows.run') ? <RunWorkflowButton workflow={workflow} variant='default' size='default' /> : null}
          {manage ? (
            <>
              <Button variant='outline' disabled={setEnabled.isPending} onClick={() => setEnabled.mutate(!workflow.isEnabled)}>
                {workflow.isEnabled ? '停用' : '启用'}
              </Button>
              <Button variant='outline' asChild>
                <Link href={`/workflows/edit?id=${workflow.id}`}>编辑</Link>
              </Button>
              <Button
                variant='ghost'
                disabled={remove.isPending}
                onClick={() => confirm(`删除「${workflow.name}」？运行记录一并删除，进行中的运行会被取消。`) && remove.mutate()}
              >
                <Icons.trash className='size-4' />
              </Button>
            </>
          ) : null}
        </div>
      </div>

      <div className='grid gap-6 lg:grid-cols-3'>
        <Card className='lg:col-span-2'>
          <CardHeader>
            <CardTitle>步骤</CardTitle>
          </CardHeader>
          <CardContent>
            <ol className='grid gap-3'>
              {workflow.steps.map((step, index) => (
                <li key={step.key} className='flex gap-3'>
                  <Badge variant='outline' className='h-6 font-mono'>
                    {index + 1}
                  </Badge>
                  <div className='min-w-0 flex-1'>
                    <p className='font-medium'>
                      {step.name}
                      <span className='text-muted-foreground ml-2 text-xs font-normal'>{workflowStepKind[step.kind ?? 'action'].label}</span>
                    </p>
                    <p className='text-muted-foreground line-clamp-2 text-sm'>{stepDetail(step)}</p>
                    {step.when ? <p className='text-muted-foreground font-mono text-xs'>条件：{step.when}</p> : null}
                  </div>
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>概览</CardTitle>
          </CardHeader>
          <CardContent className='space-y-2 text-sm'>
            <Row label='触发方式' value={workflowTriggerKind[workflow.trigger.kind ?? 'manual']} />
            {workflow.trigger.kind && workflow.trigger.kind !== 'manual' ? <Row label='规则' value={describeTrigger(workflow.trigger, catalog.data?.events)} /> : null}
            {workflow.nextRunAt ? <Row label='下次运行' value={formatTime(workflow.nextRunAt)} /> : null}
            <Row label='运行身份' value={`${workflow.owner.name ?? '—'}${workflow.owner.kind === 'agent' ? '（智能体）' : ''}`} />
            <Row label='版本' value={workflow.version} />
            <Row label='更新时间' value={formatTime(workflow.updatedAt)} />
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>运行记录</CardTitle>
        </CardHeader>
        <CardContent>
          {runs.data?.items.length ? (
            <>
              <WorkflowRunTable runs={runs.data.items} showWorkflow={false} />
              <div className='text-muted-foreground flex items-center justify-end gap-2 pt-4 text-sm'>
                第 {page} / {pages} 页
                <Button size='sm' variant='outline' disabled={page <= 1} onClick={() => setPage(page - 1)}>
                  上一页
                </Button>
                <Button size='sm' variant='outline' disabled={page >= pages} onClick={() => setPage(page + 1)}>
                  下一页
                </Button>
              </div>
            </>
          ) : (
            <p className='text-muted-foreground text-sm'>还没有运行过。</p>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className='flex justify-between gap-4'>
      <span className='text-muted-foreground shrink-0'>{label}</span>
      <span className='text-right break-all'>{value}</span>
    </div>
  );
}

export default function WorkflowDetailPage() {
  return (
    <Suspense>
      <WorkflowDetail />
    </Suspense>
  );
}
