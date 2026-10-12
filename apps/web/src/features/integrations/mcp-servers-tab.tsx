'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { EmptyState } from '@/components/app/empty-state';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { formatTime, mcpApprovalMode } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { McpServerFormDialog } from './mcp-server-form-dialog';

/**
 * 外部 MCP 服务列表：每个服务的工具都会成为平台动作，供智能体、对话和工作流调用。
 */
export function McpServersTab() {
  const router = useRouter();
  const { can } = useSession();
  const [creating, setCreating] = useState(false);
  const servers = useQuery({ queryKey: queryKeys.mcpServers, queryFn: ({ signal }) => api.integrations.mcpServers(signal) });

  return (
    <div className='space-y-4'>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <p className='text-muted-foreground max-w-2xl text-sm'>
          接入支持 MCP 的外部服务（GitHub、Notion、内部系统等），它们的工具会和内置动作一样出现在智能体、对话和工作流里，统一经过权限与审批检查。
        </p>
        {can('integrations.manage') ? (
          <Button onClick={() => setCreating(true)}>
            <Icons.add />
            接入服务
          </Button>
        ) : null}
      </div>
      {servers.data?.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>服务</TableHead>
              <TableHead>工具</TableHead>
              <TableHead>智能体调用</TableHead>
              <TableHead>状态</TableHead>
              <TableHead>工具更新于</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {servers.data.map((server) => {
              const enabledTools = server.tools.filter((t) => t.enabled).length;
              return (
                <TableRow key={server.id}>
                  <TableCell>
                    <Link href={`/integrations/mcp/view?id=${server.id}`} className='font-medium hover:underline'>
                      {server.name}
                    </Link>
                    <div className='text-muted-foreground font-mono text-xs'>{server.key}</div>
                  </TableCell>
                  <TableCell className='text-sm'>
                    {enabledTools === server.tools.length ? `${server.tools.length} 个` : `${enabledTools} / ${server.tools.length} 个启用`}
                  </TableCell>
                  <TableCell className='text-sm'>{mcpApprovalMode[server.approvalMode].label}</TableCell>
                  <TableCell>
                    {!server.isEnabled ? (
                      <Badge variant='outline'>已停用</Badge>
                    ) : server.lastError ? (
                      <Badge variant='destructive' title={server.lastError}>
                        连接异常
                      </Badge>
                    ) : (
                      <Badge variant='secondary'>正常</Badge>
                    )}
                  </TableCell>
                  <TableCell className='text-muted-foreground text-sm'>{formatTime(server.toolsRefreshedAt)}</TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      ) : servers.isPending ? null : (
        <EmptyState title='还没有接入外部服务' description='接入一个 MCP 服务，智能体就能调用它的工具，例如在 GitHub 上建 issue、查 Notion 页面。' />
      )}
      <McpServerFormDialog open={creating} onOpenChange={setCreating} onSaved={(server) => router.push(`/integrations/mcp/view?id=${server.id}`)} />
    </div>
  );
}
