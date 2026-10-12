'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { McpServerDto, UpdateMcpToolRequest } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { EmptyState } from '@/components/app/empty-state';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { McpServerFormDialog } from '@/features/integrations/mcp-server-form-dialog';
import { api } from '@/lib/api';
import { formatTime, mcpApprovalMode } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function McpServerDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const { can } = useSession();
  const manage = can('integrations.manage');
  const [editing, setEditing] = useState(false);
  const detail = useQuery({ queryKey: queryKeys.mcpServer(id), queryFn: ({ signal }) => api.integrations.getMcpServer(id, signal), enabled: !!id });

  const applied = async (server: McpServerDto) => {
    queryClient.setQueryData(queryKeys.mcpServer(server.id), server);
    await queryClient.invalidateQueries({ queryKey: queryKeys.mcpServers });
    await queryClient.invalidateQueries({ queryKey: queryKeys.actions });
  };
  const refresh = useMutation({
    mutationFn: () => api.integrations.refreshMcpServer(id),
    onSuccess: async (server) => {
      await applied(server);
      if (server.lastError) {
        toast.warning(`刷新失败：${server.lastError}`);
      } else {
        toast.success(`已刷新，共 ${server.tools.length} 个工具`);
      }
    },
  });
  const updateTool = useMutation({
    mutationFn: (request: UpdateMcpToolRequest) => api.integrations.updateMcpTool(id, request),
    onSuccess: applied,
  });
  const remove = useMutation({
    mutationFn: () => api.integrations.deleteMcpServer(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['integrations'] });
      await queryClient.invalidateQueries({ queryKey: queryKeys.actions });
      toast.success('已删除');
      router.push('/integrations');
    },
  });

  if (detail.isPending && id) {
    return <Skeleton className='m-6 h-96' />;
  }

  const server = detail.data;
  if (!server) {
    return <p className='text-muted-foreground p-6'>服务不存在或已被删除。</p>;
  }

  return (
    <div className='space-y-6 p-6'>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <Link href='/integrations' className='text-muted-foreground inline-flex items-center gap-1 text-sm hover:underline'>
            <Icons.chevronLeft className='size-4' />
            集成
          </Link>
          <div className='flex flex-wrap items-center gap-2'>
            <h1 className='text-2xl font-semibold tracking-tight'>{server.name}</h1>
            {server.isEnabled ? null : <Badge variant='outline'>已停用</Badge>}
          </div>
          <div className='text-muted-foreground flex flex-wrap items-center gap-x-3 gap-y-1 text-sm'>
            <span className='font-mono'>{server.key}</span>
            <span className='font-mono break-all'>{server.url}</span>
            <span>工具更新于 {formatTime(server.toolsRefreshedAt)}</span>
          </div>
        </div>
        {manage ? (
          <div className='flex gap-2'>
            <Button variant='outline' disabled={refresh.isPending} onClick={() => refresh.mutate()}>
              {refresh.isPending ? <Icons.spinner className='animate-spin' /> : <Icons.refresh />}
              刷新工具
            </Button>
            <Button variant='outline' onClick={() => setEditing(true)}>
              <Icons.edit />
              编辑
            </Button>
            <Button
              variant='ghost'
              aria-label='删除服务'
              disabled={remove.isPending}
              onClick={() =>
                void confirm({
                  title: `删除「${server.name}」？`,
                  description: '它的工具会从动作列表里移除；被工作流引用时无法删除。',
                  confirmLabel: '删除',
                }).then((ok) => ok && remove.mutate())
              }
            >
              <Icons.trash />
            </Button>
          </div>
        ) : null}
      </div>

      {server.lastError ? (
        <Alert variant='destructive'>
          <Icons.warning />
          <AlertTitle>{server.toolsRefreshedAt ? '最近一次刷新失败，正在沿用上次读取的工具' : '还没能连上这个服务'}</AlertTitle>
          <AlertDescription>{server.lastError}</AlertDescription>
        </Alert>
      ) : null}

      <div className='grid gap-4 md:grid-cols-2'>
        <Card>
          <CardHeader>
            <CardTitle className='text-base'>智能体调用</CardTitle>
          </CardHeader>
          <CardContent className='space-y-1 text-sm'>
            <div className='font-medium'>{mcpApprovalMode[server.approvalMode].label}</div>
            <p className='text-muted-foreground'>{mcpApprovalMode[server.approvalMode].hint}</p>
            {server.description ? <p className='pt-2 whitespace-pre-wrap'>{server.description}</p> : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle className='text-base'>请求头</CardTitle>
          </CardHeader>
          <CardContent className='text-sm'>
            {server.headers.length ? (
              <dl className='grid gap-1.5'>
                {server.headers.map((h) => (
                  <div key={h.name} className='flex gap-2 font-mono text-xs'>
                    <dt className='shrink-0 font-medium'>{h.name}:</dt>
                    <dd className='text-muted-foreground min-w-0 break-all'>{h.value}</dd>
                  </div>
                ))}
              </dl>
            ) : (
              <p className='text-muted-foreground'>没有设置请求头。</p>
            )}
          </CardContent>
        </Card>
      </div>

      <section className='space-y-3'>
        <div className='flex flex-wrap items-baseline justify-between gap-2'>
          <h2 className='text-lg font-semibold'>工具</h2>
          <p className='text-muted-foreground max-w-2xl text-sm'>
            停用的工具不会出现在智能体、对话和工作流的动作列表里。确认为只读的工具，在「只读工具免审批」模式下智能体可以直接调用；服务自己标注的只读只作参考，确认前请看清工具说明。
          </p>
        </div>
        {server.tools.length ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>工具</TableHead>
                <TableHead>说明</TableHead>
                <TableHead>类型</TableHead>
                <TableHead>只读</TableHead>
                <TableHead>启用</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {server.tools.map((tool) => (
                <TableRow key={tool.name}>
                  <TableCell className='align-top'>
                    <div className='font-medium'>{tool.title ?? tool.name}</div>
                    <div className='text-muted-foreground font-mono text-xs'>{tool.key}</div>
                  </TableCell>
                  <TableCell className='max-w-md align-top'>
                    <p className='text-muted-foreground line-clamp-3 text-sm whitespace-pre-wrap'>{tool.description || '—'}</p>
                    <details className='mt-1'>
                      <summary className='text-muted-foreground hover:text-foreground cursor-pointer text-xs'>参数</summary>
                      <pre className='bg-muted mt-1 max-h-64 overflow-auto rounded-md p-2 font-mono text-xs'>{JSON.stringify(tool.inputSchema, null, 2)}</pre>
                    </details>
                  </TableCell>
                  <TableCell className='align-top'>
                    <div className='flex flex-wrap gap-1'>
                      <Badge variant={tool.readOnly ? 'secondary' : 'outline'}>{tool.readOnly ? '只读' : '可能修改数据'}</Badge>
                      {tool.readOnlyHint && !tool.readOnly ? (
                        <Badge variant='outline' title='服务自己声明这个工具只读，只作参考；确认后打开「只读」。'>
                          服务标注只读
                        </Badge>
                      ) : null}
                      {tool.requiresApproval ? <Badge variant='outline'>智能体需审批</Badge> : null}
                    </div>
                  </TableCell>
                  <TableCell className='align-top'>
                    <Switch
                      aria-label={`确认 ${tool.name} 只读`}
                      checked={tool.readOnly}
                      disabled={!manage || updateTool.isPending}
                      onCheckedChange={(readOnly) => updateTool.mutate({ tool: tool.name, readOnly })}
                    />
                  </TableCell>
                  <TableCell className='align-top'>
                    <Switch
                      aria-label={`启用 ${tool.name}`}
                      checked={tool.enabled}
                      disabled={!manage || updateTool.isPending}
                      onCheckedChange={(enabled) => updateTool.mutate({ tool: tool.name, enabled })}
                    />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : (
          <EmptyState title='还没有读取到工具' description={server.lastError ? '连上服务后点「刷新工具」重新读取。' : '这个服务没有提供工具。'} />
        )}
      </section>

      <McpServerFormDialog open={editing} server={server} onOpenChange={setEditing} onSaved={applied} />
    </div>
  );
}

export default function McpServerPage() {
  return (
    <Suspense>
      <McpServerDetail />
    </Suspense>
  );
}
