'use client';

import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Badge } from '@/components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { catalogQueries } from '@/lib/catalogs';
import { formatTime, formatUsd } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

export function UsageTab() {
  const { can } = useSession();
  const usage = useQuery({ queryKey: queryKeys.usage, queryFn: ({ signal }) => api.usage.summary(signal), enabled: can('platform.usage.view') });
  const models = useQuery({ ...catalogQueries.models(), enabled: can('agents.view') });

  return (
    <div className='space-y-6'>
      {usage.data ? (
        <div className='grid gap-6 lg:grid-cols-2'>
          <Card>
            <CardHeader>
              <CardTitle>本月花费 {formatUsd(usage.data.totalCostUsd)}</CardTitle>
              <p className='text-muted-foreground text-xs'>自 {formatTime(usage.data.periodStart)} 起</p>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>智能体</TableHead>
                    <TableHead className='text-right'>已用</TableHead>
                    <TableHead className='text-right'>月预算</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {usage.data.byAgent.map((a) => (
                    <TableRow key={a.agentId}>
                      <TableCell>{a.name}</TableCell>
                      <TableCell className='text-right'>{formatUsd(a.costUsd)}</TableCell>
                      <TableCell className='text-muted-foreground text-right'>{a.budgetUsd > 0 ? formatUsd(a.budgetUsd) : '不限'}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>按模型</CardTitle>
            </CardHeader>
            <CardContent>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>模型</TableHead>
                    <TableHead className='text-right'>输入</TableHead>
                    <TableHead className='text-right'>输出</TableHead>
                    <TableHead className='text-right'>花费</TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {usage.data.byModel.map((m) => (
                    <TableRow key={`${m.provider}/${m.model}`}>
                      <TableCell className='font-mono text-xs'>
                        {m.provider}/{m.model}
                      </TableCell>
                      <TableCell className='text-right'>{m.inputTokens.toLocaleString()}</TableCell>
                      <TableCell className='text-right'>{m.outputTokens.toLocaleString()}</TableCell>
                      <TableCell className='text-right'>{formatUsd(m.costUsd)}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </CardContent>
          </Card>
        </div>
      ) : null}
      {models.data ? (
        <Card>
          <CardHeader>
            <CardTitle>模型供应商</CardTitle>
            <p className='text-muted-foreground text-xs'>
              默认：{models.data.defaultProvider} / {models.data.defaultModel}。在后端配置 Ai:Providers 添加供应商和密钥。
            </p>
          </CardHeader>
          <CardContent className='grid gap-3 md:grid-cols-2'>
            {models.data.providers.map((p) => (
              <div key={p.name} className='rounded-md border p-3 text-sm'>
                <div className='flex items-center justify-between'>
                  <span className='font-medium'>{p.name}</span>
                  <Badge variant={p.configured ? 'secondary' : 'outline'}>{p.configured ? '已配置' : '缺少密钥'}</Badge>
                </div>
                <p className='text-muted-foreground mt-1 text-xs'>类型 {p.kind}</p>
                <div className='mt-2 flex flex-wrap gap-1'>
                  {p.models.map((m) => (
                    <Badge key={m} variant='outline' className='font-mono text-[11px]'>
                      {m}
                    </Badge>
                  ))}
                </div>
              </div>
            ))}
          </CardContent>
        </Card>
      ) : null}
    </div>
  );
}
