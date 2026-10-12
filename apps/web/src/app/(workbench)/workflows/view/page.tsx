'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { DetailState } from '@/components/app/query-state';
import { useConfirm } from '@/components/app/confirm-provider';
import { Pager } from '@/components/app/pager';
import { useSession } from '@/components/app/session-provider';
import { ValueRow } from '@/components/app/info-row';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { describeTrigger } from '@/features/workflows/describe';
import { RunWorkflowButton } from '@/features/workflows/run-workflow-dialog';
import { StepTree } from '@/features/workflows/step-tree';
import { WebhookCard } from '@/features/workflows/webhook-card';
import { WorkflowRunTable } from '@/features/workflows/workflow-run-table';
import { api } from '@/lib/api';
import { formatTime, workflowTriggerKind } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 20;

function WorkflowDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
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
    return <DetailState id={id} query={detail} subject='工作流' back={{ href: '/workflows', label: '自动化' }} />;
  }

  const workflow = detail.data;
  const manage = can('workflows.manage');
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
            <Badge variant={workflow.isEnabled ? 'success' : 'outline'}>{workflow.isEnabled ? '已启用' : '已停用'}</Badge>
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
                aria-label='删除工作流'
                onClick={() =>
                  void confirm({ title: `删除「${workflow.name}」？`, description: '运行记录一并删除，进行中的运行会被取消。', confirmLabel: '删除' }).then(
                    (ok) => ok && remove.mutate(),
                  )
                }
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
            <StepTree steps={workflow.steps} actions={actions.data} agents={agents.data} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>概览</CardTitle>
          </CardHeader>
          <CardContent className='space-y-2 text-sm'>
            <ValueRow label='触发方式' value={workflowTriggerKind[workflow.trigger.kind ?? 'manual']} />
            {workflow.trigger.kind && workflow.trigger.kind !== 'manual' ? <ValueRow label='规则' value={describeTrigger(workflow.trigger, catalog.data?.events)} /> : null}
            {workflow.nextRunAt ? <ValueRow label='下次运行' value={formatTime(workflow.nextRunAt)} /> : null}
            <ValueRow label='运行身份' value={`${workflow.owner.name ?? '—'}${workflow.owner.kind === 'agent' ? '（智能体）' : ''}`} />
            <ValueRow label='版本' value={workflow.version} />
            <ValueRow label='更新时间' value={formatTime(workflow.updatedAt)} />
          </CardContent>
        </Card>
      </div>

      {workflow.trigger.kind === 'webhook' ? <WebhookCard workflow={workflow} canManage={manage} /> : null}

      <Card>
        <CardHeader>
          <CardTitle>运行记录</CardTitle>
        </CardHeader>
        <CardContent>
          {runs.data?.items.length ? (
            <>
              <WorkflowRunTable runs={runs.data.items} showWorkflow={false} />
              <Pager page={page} pageSize={PAGE_SIZE} total={runs.data.total} onChange={setPage} />
            </>
          ) : (
            <p className='text-muted-foreground text-sm'>还没有运行过。</p>
          )}
        </CardContent>
      </Card>
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
