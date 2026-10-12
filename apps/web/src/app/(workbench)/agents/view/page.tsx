'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { useConfirm } from '@/components/app/confirm-provider';
import { EmptyState } from '@/components/app/empty-state';
import { useFeatureCatalog } from '@/components/app/feature-checklist';
import { ValueRow } from '@/components/app/info-row';
import { PageHeader } from '@/components/app/page-header';
import { DetailState } from '@/components/app/query-state';
import { RawKeyTooltip } from '@/components/app/raw-key';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { AgentFormDialog } from '@/features/agents/agent-form-dialog';
import { RunTable } from '@/features/runs/run-table';
import { api } from '@/lib/api';
import { agentStatus, formatTime, formatUsd, priority, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function AgentDetail() {
  const id = useSearchParams().get('id') ?? '';
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const [editing, setEditing] = useState(false);
  const [reason, setReason] = useState('');
  const { label } = useFeatureCatalog();
  const agent = useQuery({ queryKey: queryKeys.agent(id), queryFn: ({ signal }) => api.agents.get(id, signal), enabled: !!id });
  const runs = useQuery({ queryKey: queryKeys.runs(id), queryFn: ({ signal }) => api.runs.list(id, 1, 20, signal), enabled: !!id });
  const tasks = useQuery({
    queryKey: queryKeys.workItems({ assigneeAgentId: id, openOnly: true }),
    queryFn: ({ signal }) => api.workItems.list(undefined, id, undefined, undefined, true, 1, 50, undefined, undefined, undefined, undefined, signal),
    enabled: !!id,
  });

  const refresh = () => queryClient.invalidateQueries({ queryKey: queryKeys.agents });
  const wake = useMutation({
    mutationFn: () => api.agents.wake(id, { reason: reason.trim() || '手动唤醒' }),
    onSuccess: () => {
      setReason('');
      toast.success('已唤醒，稍后可在执行记录里看到过程');
      void refresh();
    },
  });
  const pause = useMutation({
    mutationFn: (paused: boolean) => (paused ? api.agents.pause(id) : api.agents.resume(id)),
    onSuccess: (_, paused) => {
      toast.success(paused ? '已暂停' : '已恢复');
      void refresh();
    },
  });
  const terminate = useMutation({ mutationFn: () => api.agents.terminate(id), meta: { successMessage: '已终止' }, onSuccess: refresh });

  if (!agent.data) {
    return <DetailState id={id} query={agent} subject='智能体' back={{ href: '/agents', label: '智能体' }} />;
  }

  const a = agent.data;
  const manageable = can('agents.manage') && a.status !== 'terminated';

  return (
    <div className='space-y-6 p-6'>
      <Link href='/agents' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        智能体
      </Link>
      <PageHeader
        title={a.name}
        description={a.title}
        actions={
          <>
            {can('chat.use') && a.status !== 'terminated' ? (
              <Button variant='outline' asChild>
                <Link href={`/chat?agent=${a.id}`}>
                  <Icons.chat />
                  对话
                </Link>
              </Button>
            ) : null}
            {manageable ? (
              <>
                <Button variant='outline' onClick={() => setEditing(true)}>
                  <Icons.edit />
                  编辑
                </Button>
                <Button variant='outline' disabled={pause.isPending} onClick={() => pause.mutate(a.status !== 'paused')}>
                  {pause.isPending ? <Icons.spinner className='animate-spin' /> : null}
                  {a.status === 'paused' ? '恢复' : '暂停'}
                </Button>
                <Button
                  variant='destructive'
                  disabled={terminate.isPending}
                  onClick={() =>
                    void confirm({ title: `终止 ${a.name}？`, description: '终止后不能恢复，下属会改为向其上级汇报。', confirmLabel: '终止' }).then(
                      (ok) => ok && terminate.mutate(),
                    )
                  }
                >
                  终止
                </Button>
              </>
            ) : null}
          </>
        }
      />
      <div className='grid gap-6 lg:grid-cols-3'>
        <Card className='lg:col-span-2'>
          <CardHeader>
            <CardTitle className='flex items-center gap-2'>
              岗位职责 <StatusBadge meta={agentStatus[a.status]} />
            </CardTitle>
          </CardHeader>
          <CardContent className='space-y-4 text-sm'>
            <p className='whitespace-pre-wrap'>{a.instructions || '（未填写）'}</p>
            {a.pauseReason ? <p className='text-muted-foreground'>暂停原因：{a.pauseReason}</p> : null}
            {a.lastError ? <p className='text-destructive'>最近错误：{a.lastError}</p> : null}
            <div className='space-y-2'>
              <p className='text-muted-foreground text-xs'>权限</p>
              <div className='flex flex-wrap gap-1'>
                {a.permissions.length ? (
                  a.permissions.map((p) => (
                    <RawKeyTooltip key={p} value={p}>
                      <Badge variant='outline'>{label(p)}</Badge>
                    </RawKeyTooltip>
                  ))
                ) : (
                  <span className='text-muted-foreground'>没有授予任何权限</span>
                )}
              </div>
            </div>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>运行情况</CardTitle>
          </CardHeader>
          <CardContent className='space-y-2 text-sm'>
            <ValueRow label='模型' value={`${a.model}（${a.modelProvider}）`} />
            <ValueRow label='本月花费' value={`${formatUsd(a.spentThisMonthUsd)}${a.monthlyBudgetUsd > 0 ? ` / ${formatUsd(a.monthlyBudgetUsd)}` : ''}`} />
            <ValueRow label='定时心跳' value={a.heartbeatIntervalSeconds ? `每 ${a.heartbeatIntervalSeconds} 秒` : '关闭'} />
            <ValueRow label='上次心跳' value={formatTime(a.lastHeartbeatAt)} />
            {can('agents.wake') && a.status !== 'terminated' ? (
              <form
                className='flex gap-2 pt-2'
                onSubmit={(event) => {
                  event.preventDefault();
                  wake.mutate();
                }}
              >
                <Input value={reason} onChange={(e) => setReason(e.target.value)} placeholder='唤醒原因（可选）' aria-label='唤醒原因' />
                <Button type='submit' disabled={wake.isPending}>
                  唤醒
                </Button>
              </form>
            ) : null}
          </CardContent>
        </Card>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>负责中的任务</CardTitle>
        </CardHeader>
        <CardContent>
          {tasks.data?.items.length ? (
            <ul className='divide-y text-sm'>
              {tasks.data.items.map((task) => (
                <li key={task.id} className='flex items-center gap-3 py-2'>
                  <span className='text-muted-foreground font-mono'>{task.key}</span>
                  <Link href={`/tasks/view?id=${task.id}`} className='flex-1 truncate hover:underline'>
                    {task.title}
                  </Link>
                  <span className='text-muted-foreground'>{priority[task.priority]}</span>
                  <StatusBadge meta={workItemStatus[task.status]} />
                </li>
              ))}
            </ul>
          ) : (
            <p className='text-muted-foreground text-sm'>暂无进行中的任务。</p>
          )}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>最近执行</CardTitle>
        </CardHeader>
        <CardContent>
          {runs.data?.items.length ? <RunTable runs={runs.data.items} showAgent={false} /> : <EmptyState title='还没有执行记录' />}
        </CardContent>
      </Card>
      <AgentFormDialog open={editing} onOpenChange={setEditing} agent={a} />
    </div>
  );
}


export default function AgentDetailPage() {
  return (
    <Suspense>
      <AgentDetail />
    </Suspense>
  );
}
