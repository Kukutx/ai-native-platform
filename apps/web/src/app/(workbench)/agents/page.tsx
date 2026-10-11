'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { AgentFormDialog } from '@/features/agents/agent-form-dialog';
import { OrgChart } from '@/features/agents/org-chart';
import { api } from '@/lib/api';
import { agentStatus, formatUsd } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

export default function AgentsPage() {
  const { can } = useSession();
  const [creating, setCreating] = useState(false);
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const orgChart = useQuery({ queryKey: queryKeys.orgChart, queryFn: ({ signal }) => api.agents.orgChart(signal) });
  const active = agents.data?.filter((a) => a.status !== 'terminated') ?? [];

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='智能体'
        description='你的 AI 员工：每位都有岗位、上级、权限和预算，被指派任务或收到消息时自动醒来工作。'
        actions={
          can('agents.manage') ? (
            <Button onClick={() => setCreating(true)}>
              <Icons.add />
              招聘智能体
            </Button>
          ) : null
        }
      />
      <Tabs defaultValue='list'>
        <TabsList>
          <TabsTrigger value='list'>成员</TabsTrigger>
          <TabsTrigger value='org'>组织架构</TabsTrigger>
        </TabsList>
        <TabsContent value='list' className='mt-4'>
          {agents.isPending ? (
            <Skeleton className='h-32 w-full' />
          ) : active.length === 0 ? (
            <EmptyState title='还没有智能体' description='招聘第一位 AI 员工，开始分配工作。' />
          ) : (
            <div className='grid gap-4 sm:grid-cols-2 xl:grid-cols-3'>
              {active.map((agent) => (
                <Link key={agent.id} href={`/agents/view?id=${agent.id}`}>
                  <Card className='hover:border-foreground/30 h-full transition-colors'>
                    <CardHeader>
                      <div className='flex items-start justify-between gap-2'>
                        <div>
                          <CardTitle>{agent.name}</CardTitle>
                          <CardDescription>{agent.title}</CardDescription>
                        </div>
                        <StatusBadge meta={agentStatus[agent.status]} />
                      </div>
                    </CardHeader>
                    <CardContent className='text-muted-foreground space-y-1 text-sm'>
                      <p className='line-clamp-2'>{agent.instructions || '（未填写岗位职责）'}</p>
                      <p>
                        {agent.model} · 本月 {formatUsd(agent.spentThisMonthUsd)}
                        {agent.monthlyBudgetUsd > 0 ? ` / ${formatUsd(agent.monthlyBudgetUsd)}` : ''}
                      </p>
                    </CardContent>
                  </Card>
                </Link>
              ))}
            </div>
          )}
        </TabsContent>
        <TabsContent value='org' className='mt-4'>
          {orgChart.data ? <OrgChart nodes={orgChart.data} /> : <Skeleton className='h-32 w-full' />}
        </TabsContent>
      </Tabs>
      <AgentFormDialog open={creating} onOpenChange={setCreating} />
    </div>
  );
}
