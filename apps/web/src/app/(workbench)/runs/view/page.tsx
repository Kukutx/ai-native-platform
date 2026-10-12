'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { RunEventDto } from '@ainative/api-client';
import type { RealtimeClient } from '@ainative/api-client/realtime';
import { DetailState } from '@/components/app/query-state';
import { StatusBadge } from '@/components/app/status-badge';
import { ValueRow } from '@/components/app/info-row';
import { Icons } from '@/components/icons';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { RunTimeline } from '@/features/runs/run-timeline';
import { api, loadRealtime } from '@/lib/api';
import { formatTime, formatUsd, runStatus, wakeupSource } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const finished = new Set(['succeeded', 'failed', 'cancelled', 'timed_out']);

function RunDetail() {
  const id = useSearchParams().get('id') ?? '';
  const queryClient = useQueryClient();
  const detail = useQuery({ queryKey: queryKeys.run(id), queryFn: ({ signal }) => api.runs.get(id, signal), enabled: !!id });
  const [live, setLive] = useState<{ events: RunEventDto[]; text: string }>({ events: [], text: '' });
  const active = !!detail.data && !finished.has(detail.data.run.status);

  useEffect(() => {
    if (!id || !active) {
      return;
    }

    let disposed = false;
    let cleanup: (() => void) | undefined;
    const subscribe = (realtime: RealtimeClient) => {
      const off = realtime.onRunEvent((event) => {
        if (event.runId !== id) {
          return;
        }

        if (event.seq === 0) {
          // 序号 0 是逐字增量，不落库；收到下一条持久化事件时清空。
          const delta = (event.data as { delta?: string } | undefined)?.delta ?? '';
          setLive((s) => ({ ...s, text: s.text + delta }));
          return;
        }

        setLive((s) => ({ events: [...s.events.filter((e) => e.seq !== event.seq), event], text: '' }));
        const status = (event.data as { status?: string } | undefined)?.status;
        if (event.type === 'error' || (event.type === 'status' && status && finished.has(status))) {
          void queryClient.invalidateQueries({ queryKey: queryKeys.run(id) });
        }
      });
      void realtime
        .start()
        .then(() => realtime.subscribeRun(id))
        .catch(() => {
          // 实时通道不可用时依赖变化通知刷新详情。
        });
      cleanup = () => {
        off();
        void realtime.unsubscribeRun(id).catch(() => {});
      };
    };

    // 实时通道客户端按需加载，加载完成前离开页面时不再订阅。
    loadRealtime()
      .then((realtime) => {
        if (!disposed) {
          subscribe(realtime);
        }
      })
      .catch(() => {
        // 脚本加载失败时依赖变化通知或手动刷新查看最新过程。
      });

    return () => {
      disposed = true;
      cleanup?.();
    };
  }, [id, active, queryClient]);

  const events = useMemo(() => {
    const merged = new Map<number, RunEventDto>();
    for (const event of [...(detail.data?.events ?? []), ...live.events]) {
      merged.set(event.seq, event);
    }

    return [...merged.values()].sort((a, b) => a.seq - b.seq);
  }, [detail.data, live.events]);

  if (!detail.data) {
    return <DetailState id={id} query={detail} subject='执行记录' back={{ href: '/runs', label: '执行记录' }} />;
  }

  const { run, wakeup } = detail.data;

  return (
    <div className='space-y-6 p-6'>
      <Link href='/runs' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        执行记录
      </Link>
      <div className='flex flex-wrap items-center gap-3'>
        <h1 className='text-2xl font-semibold tracking-tight'>
          <Link href={`/agents/view?id=${run.agentId}`} className='hover:underline'>
            {run.agentName}
          </Link>
          <span className='text-muted-foreground'> 的一次执行</span>
        </h1>
        <StatusBadge meta={runStatus[run.status]} />
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <Card className='lg:col-span-2'>
          <CardHeader>
            <CardTitle>过程</CardTitle>
          </CardHeader>
          <CardContent>
            {events.length || live.text ? (
              <RunTimeline events={events} liveText={active ? live.text : undefined} />
            ) : (
              <p className='text-muted-foreground text-sm'>{active ? '等待智能体开始…' : '没有记录到过程事件。'}</p>
            )}
          </CardContent>
        </Card>
        <div className='space-y-6'>
          <Card>
            <CardHeader>
              <CardTitle>概览</CardTitle>
            </CardHeader>
            <CardContent className='space-y-2 text-sm'>
              <ValueRow label='模型' value={`${run.provider} / ${run.model}`} />
              <ValueRow label='步数' value={run.steps} />
              <ValueRow label='输入 tokens' value={run.inputTokens.toLocaleString()} />
              <ValueRow label='输出 tokens' value={run.outputTokens.toLocaleString()} />
              <ValueRow label='花费' value={formatUsd(run.costUsd)} />
              <ValueRow label='开始' value={formatTime(run.startedAt)} />
              <ValueRow label='结束' value={formatTime(run.finishedAt)} />
              {run.workItemId ? (
                <ValueRow
                  label='关联任务'
                  value={
                    <Link href={`/tasks/view?id=${run.workItemId}`} className='underline'>
                      查看
                    </Link>
                  }
                />
              ) : null}
              {run.error ? <p className='text-destructive pt-2'>{run.error}</p> : null}
            </CardContent>
          </Card>
          {wakeup ? (
            <Card>
              <CardHeader>
                <CardTitle>唤醒原因</CardTitle>
              </CardHeader>
              <CardContent className='space-y-3 text-sm'>
                {wakeup.items.map((item, index) => (
                  <div key={index}>
                    <p className='text-muted-foreground text-xs'>
                      {wakeupSource[item.source]} · {formatTime(item.requestedAt)}
                    </p>
                    <p>{item.reason}</p>
                  </div>
                ))}
                {wakeup.coalescedCount > 0 ? (
                  <p className='text-muted-foreground text-xs'>{wakeup.coalescedCount + 1} 次唤醒被合并为这一次执行。</p>
                ) : null}
              </CardContent>
            </Card>
          ) : null}
        </div>
      </div>
    </div>
  );
}


export default function RunDetailPage() {
  return (
    <Suspense>
      <RunDetail />
    </Suspense>
  );
}
