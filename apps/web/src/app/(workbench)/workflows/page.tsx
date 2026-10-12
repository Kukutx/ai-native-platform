'use client';

import { useState } from 'react';
import Link from 'next/link';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { Pager } from '@/components/app/pager';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { describeTrigger } from '@/features/workflows/describe';
import { RunWorkflowButton } from '@/features/workflows/run-workflow-dialog';
import { WorkflowRunTable } from '@/features/workflows/workflow-run-table';
import { api } from '@/lib/api';
import { formatTime, workflowRunStatus, workflowTriggerKind } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 20;

export default function WorkflowsPage() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const workflows = useQuery({ queryKey: queryKeys.workflows, queryFn: ({ signal }) => api.workflows.list(signal) });
  const catalog = useQuery({ queryKey: queryKeys.workflowCatalog, queryFn: ({ signal }) => api.workflows.catalog(signal) });
  const runs = useQuery({
    queryKey: queryKeys.workflowRuns({ page }),
    queryFn: ({ signal }) => api.workflows.runs(undefined, undefined, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });
  const setEnabled = useMutation({
    mutationFn: ({ id, enabled }: { id: string; enabled: boolean }) => api.workflows.setEnabled(id, { enabled }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: queryKeys.workflows }),
  });
  const manage = can('workflows.manage');
  const run = can('workflows.run');

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='自动化'
        description='把固定流程交给平台：由事件、定时或手动触发，按步骤执行动作、派任务给智能体、调用模型。'
        actions={
          manage ? (
            <Button asChild>
              <Link href='/workflows/edit'>
                <Icons.add className='size-4' />
                新建工作流
              </Link>
            </Button>
          ) : null
        }
      />
      {workflows.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : workflows.isError && !workflows.data ? (
        <ErrorState query={workflows} />
      ) : workflows.data?.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>名称</TableHead>
                  <TableHead>触发</TableHead>
                  <TableHead className='text-right'>步骤</TableHead>
                  <TableHead>上次运行</TableHead>
                  <TableHead>下次运行</TableHead>
                  <TableHead>状态</TableHead>
                  <TableHead className='text-right'>操作</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {workflows.data.map((w) => (
                  <TableRow key={w.id}>
                    <TableCell className='max-w-xs'>
                      <Link href={`/workflows/view?id=${w.id}`} className='font-medium hover:underline'>
                        {w.name}
                      </Link>
                      {w.description ? <p className='text-muted-foreground line-clamp-1 text-xs'>{w.description}</p> : null}
                    </TableCell>
                    <TableCell className='max-w-xs'>
                      <p className='text-sm'>{workflowTriggerKind[w.trigger.kind ?? 'manual']}</p>
                      {w.trigger.kind && w.trigger.kind !== 'manual' ? (
                        <p className='text-muted-foreground line-clamp-1 text-xs'>{describeTrigger(w.trigger, catalog.data?.events)}</p>
                      ) : null}
                    </TableCell>
                    <TableCell className='text-right'>{w.steps.length}</TableCell>
                    <TableCell>
                      {w.lastRun ? (
                        <Link href={`/workflows/run?id=${w.lastRun.id}`} className='flex items-center gap-2'>
                          <StatusBadge meta={workflowRunStatus[w.lastRun.status]} />
                          <span className='text-muted-foreground text-xs'>{formatTime(w.lastRun.createdAt)}</span>
                        </Link>
                      ) : (
                        <span className='text-muted-foreground text-xs'>从未运行</span>
                      )}
                    </TableCell>
                    <TableCell className='text-muted-foreground text-xs'>{w.nextRunAt ? formatTime(w.nextRunAt) : '—'}</TableCell>
                    <TableCell>
                      {manage ? (
                        <Button
                          size='sm'
                          variant='ghost'
                          disabled={setEnabled.isPending}
                          onClick={() => setEnabled.mutate({ id: w.id, enabled: !w.isEnabled })}
                          title={w.isEnabled ? '点击停用' : '点击启用'}
                        >
                          <Badge variant={w.isEnabled ? 'success' : 'outline'}>{w.isEnabled ? '已启用' : '已停用'}</Badge>
                        </Button>
                      ) : (
                        <Badge variant={w.isEnabled ? 'success' : 'outline'}>{w.isEnabled ? '已启用' : '已停用'}</Badge>
                      )}
                    </TableCell>
                    <TableCell className='text-right'>
                      <div className='flex justify-end gap-2'>
                        {run ? <RunWorkflowButton workflow={w} /> : null}
                        {manage ? (
                          <Button size='sm' variant='ghost' asChild>
                            <Link href={`/workflows/edit?id=${w.id}`}>编辑</Link>
                          </Button>
                        ) : null}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      ) : (
        <EmptyState title='还没有工作流' description='例如：任务完成时自动通知、每个工作日早上让智能体整理待办、新客户进来时自动建跟进任务。'>
          {manage ? (
            <Button asChild>
              <Link href='/workflows/edit'>新建工作流</Link>
            </Button>
          ) : null}
        </EmptyState>
      )}

      {runs.data?.items.length ? (
        <Card>
          <CardHeader>
            <CardTitle>最近运行</CardTitle>
          </CardHeader>
          <CardContent>
            <WorkflowRunTable runs={runs.data.items} />
            <Pager page={page} pageSize={PAGE_SIZE} total={runs.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
