'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ApprovalDto } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { approvalStatus, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

export default function ApprovalsPage() {
  const [tab, setTab] = useState<'pending' | 'all'>('pending');
  const status = tab === 'pending' ? 'pending' : undefined;
  const approvals = useQuery({
    queryKey: queryKeys.approvals(status),
    queryFn: ({ signal }) => api.approvals.list(status, signal),
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='审批'
        description='智能体执行高风险动作（招聘新智能体、超出权限的操作等）前，需要人工批准。批准后动作以申请人的身份执行。'
        actions={
          <Tabs value={tab} onValueChange={(v) => setTab(v as 'pending' | 'all')}>
            <TabsList>
              <TabsTrigger value='pending'>待处理</TabsTrigger>
              <TabsTrigger value='all'>全部</TabsTrigger>
            </TabsList>
          </Tabs>
        }
      />
      {approvals.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : approvals.isError && !approvals.data ? (
        <ErrorState query={approvals} />
      ) : approvals.data?.length ? (
        <div className='grid gap-4'>
          {approvals.data.map((approval) => (
            <ApprovalCard key={approval.id} approval={approval} />
          ))}
        </div>
      ) : (
        <EmptyState title={tab === 'pending' ? '没有待处理的审批' : '还没有审批记录'} />
      )}
    </div>
  );
}

function ApprovalCard({ approval }: { approval: ApprovalDto }) {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [note, setNote] = useState('');
  const decide = useMutation({
    mutationFn: (approve: boolean) => api.approvals.decide(approval.id, { approve, note: note.trim() || undefined }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['approvals'] }),
  });
  const pending = approval.status === 'pending';

  return (
    <Card>
      <CardHeader className='flex flex-row flex-wrap items-start justify-between gap-2'>
        <div className='space-y-1'>
          <CardTitle>{approval.actionTitle}</CardTitle>
          <p className='text-muted-foreground text-xs'>
            <span className='font-mono'>{approval.actionKey}</span> · {approval.requestedBy.name ?? '系统'} 申请于 {formatTime(approval.createdAt)}
          </p>
        </div>
        <StatusBadge meta={approvalStatus[approval.status]} />
      </CardHeader>
      <CardContent className='space-y-4 text-sm'>
        {approval.rationale ? (
          <div>
            <p className='text-muted-foreground mb-1 text-xs'>理由</p>
            <p className='whitespace-pre-wrap'>{approval.rationale}</p>
          </div>
        ) : null}
        <div>
          <p className='text-muted-foreground mb-1 text-xs'>参数</p>
          <pre className='bg-muted max-h-64 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(approval.input, null, 2)}</pre>
        </div>
        <div className='text-muted-foreground flex flex-wrap gap-4 text-xs'>
          {approval.runId ? (
            <Link href={`/runs/view?id=${approval.runId}`} className='underline'>
              查看申请时的执行过程
            </Link>
          ) : null}
          {approval.workItemId ? (
            <Link href={`/tasks/view?id=${approval.workItemId}`} className='underline'>
              关联任务
            </Link>
          ) : null}
        </div>
        {pending && can('approvals.decide') ? (
          <div className='space-y-2'>
            <Textarea value={note} onChange={(e) => setNote(e.target.value)} placeholder='审批意见（可选，会转达给智能体）' rows={2} />
            <div className='flex gap-2'>
              <Button disabled={decide.isPending} onClick={() => decide.mutate(true)}>
                批准并执行
              </Button>
              <Button variant='outline' disabled={decide.isPending} onClick={() => decide.mutate(false)}>
                驳回
              </Button>
            </div>
          </div>
        ) : null}
        {!pending ? (
          <div className='space-y-1 border-t pt-3'>
            <p className='text-muted-foreground text-xs'>处理于 {formatTime(approval.decidedAt)}</p>
            {approval.decisionNote ? <p>意见：{approval.decisionNote}</p> : null}
            {approval.executionError ? <p className='text-destructive'>执行失败：{approval.executionError}</p> : null}
            {approval.result !== undefined && approval.result !== null ? (
              <pre className='bg-muted max-h-48 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(approval.result, null, 2)}</pre>
            ) : null}
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}
