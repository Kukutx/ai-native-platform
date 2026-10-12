'use client';

import { useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { Pager } from '@/components/app/pager';
import { Card, CardContent } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { RunTable } from '@/features/runs/run-table';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const ALL = '__all__';
const PAGE_SIZE = 30;

export default function RunsPage() {
  const [agentId, setAgentId] = useState<string>(ALL);
  const [page, setPage] = useState(1);
  const filter = agentId === ALL ? undefined : agentId;
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const runs = useQuery({
    queryKey: [...queryKeys.runs(filter), page],
    queryFn: ({ signal }) => api.runs.list(filter, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='执行记录'
        description='智能体每次被唤醒都会留下一条完整的执行记录：思考、调用了哪些动作、花了多少钱。'
        actions={
          <Select
            value={agentId}
            onValueChange={(value) => {
              setAgentId(value);
              setPage(1);
            }}
          >
            <SelectTrigger className='w-48'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>全部智能体</SelectItem>
              {agents.data?.map((a) => (
                <SelectItem key={a.id} value={a.id}>
                  {a.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        }
      />
      {runs.isPending ? (
        <Skeleton className='h-64 w-full' />
      ) : runs.data?.items.length ? (
        <Card>
          <CardContent>
            <RunTable runs={runs.data.items} />
            <Pager page={page} pageSize={PAGE_SIZE} total={runs.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState title='还没有执行记录' description='给智能体指派任务或手动唤醒后，这里会出现它的执行过程。' />
      )}
    </div>
  );
}
