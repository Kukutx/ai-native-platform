'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ApiKeyDto, CreatedApiKeyDto } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { API_URL } from '@/lib/config';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const NEVER = 'never';
const MCP_URL = `${API_URL.replace(/\/+$/, '')}/mcp`;

function keyState(key: ApiKeyDto): { label: string; tone: 'secondary' | 'outline' | 'destructive' } {
  if (key.revokedAt) {
    return { label: '已吊销', tone: 'destructive' };
  }

  if (key.expiresAt && new Date(key.expiresAt) <= new Date()) {
    return { label: '已过期', tone: 'outline' };
  }

  return { label: '有效', tone: 'secondary' };
}

/**
 * 个人 API 密钥：给 Claude Code、Claude Desktop 等 MCP 客户端和脚本使用，权限不超过本人。
 */
export function ApiKeysTab() {
  const queryClient = useQueryClient();
  const keys = useQuery({ queryKey: queryKeys.apiKeys, queryFn: ({ signal }) => api.apiKeys.list(signal) });
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<CreatedApiKeyDto | null>(null);
  const revoke = useMutation({
    mutationFn: (id: string) => api.apiKeys.revoke(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys }),
  });

  return (
    <div className='space-y-4'>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <p className='text-muted-foreground max-w-2xl text-sm'>
          用 API 密钥把平台接入 Claude Code、Claude Desktop 等 MCP 客户端，或者在脚本里调用接口。密钥以你的身份执行，权限不会超过你本人；成员被停用后密钥随之失效。
        </p>
        <Button onClick={() => setCreating(true)}>
          <Icons.add />
          创建密钥
        </Button>
      </div>
      {keys.data?.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>名称</TableHead>
              <TableHead>密钥</TableHead>
              <TableHead>权限</TableHead>
              <TableHead>状态</TableHead>
              <TableHead>最近使用</TableHead>
              <TableHead>到期</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {keys.data.map((key) => {
              const state = keyState(key);
              return (
                <TableRow key={key.id}>
                  <TableCell className='font-medium'>{key.name}</TableCell>
                  <TableCell className='text-muted-foreground font-mono text-xs'>{key.prefix}_…</TableCell>
                  <TableCell>{key.features.length ? '只读' : '与本人一致'}</TableCell>
                  <TableCell>
                    <Badge variant={state.tone}>{state.label}</Badge>
                  </TableCell>
                  <TableCell className='text-muted-foreground text-sm'>{formatTime(key.lastUsedAt)}</TableCell>
                  <TableCell className='text-muted-foreground text-sm'>{key.expiresAt ? formatTime(key.expiresAt) : '永不'}</TableCell>
                  <TableCell className='text-right'>
                    {key.revokedAt ? null : (
                      <Button size='sm' variant='ghost' onClick={() => confirm(`吊销「${key.name}」？使用它的客户端会立即失去访问权限。`) && revoke.mutate(key.id)}>
                        吊销
                      </Button>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      ) : keys.isPending ? null : (
        <EmptyState title='还没有 API 密钥' description='创建一个密钥，就能在 Claude Code 里直接查任务、建任务、检索知识库。' />
      )}
      <CreateKeyDialog
        open={creating}
        onOpenChange={setCreating}
        onCreated={(key) => {
          setCreating(false);
          setCreated(key);
        }}
      />
      {created ? <CreatedKeyDialog created={created} onClose={() => setCreated(null)} /> : null}
    </div>
  );
}

function CreateKeyDialog({ open, onOpenChange, onCreated }: { open: boolean; onOpenChange: (open: boolean) => void; onCreated: (key: CreatedApiKeyDto) => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState('Claude Code');
  const [readOnly, setReadOnly] = useState(false);
  const [expires, setExpires] = useState('90');
  const create = useMutation({
    mutationFn: () => api.apiKeys.create({ name, readOnly, expiresInDays: expires === NEVER ? undefined : Number(expires) }),
    onSuccess: async (key) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys });
      onCreated(key);
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>创建 API 密钥</DialogTitle>
        </DialogHeader>
        <form
          className='grid gap-4'
          onSubmit={(e) => {
            e.preventDefault();
            create.mutate();
          }}
        >
          <div className='grid gap-2'>
            <Label htmlFor='key-name'>名称</Label>
            <Input id='key-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder='用在哪里，例如 Claude Code' />
          </div>
          <div className='grid gap-2'>
            <Label>有效期</Label>
            <Select value={expires} onValueChange={setExpires}>
              <SelectTrigger className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value='30'>30 天</SelectItem>
                <SelectItem value='90'>90 天</SelectItem>
                <SelectItem value='365'>一年</SelectItem>
                <SelectItem value={NEVER}>永不过期</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <label className='flex items-start gap-2 rounded-md border p-3 text-sm'>
            <input type='checkbox' className='accent-primary mt-0.5' checked={readOnly} onChange={(e) => setReadOnly(e.target.checked)} />
            <span>
              <span className='font-medium'>只读</span>
              <span className='text-muted-foreground block text-xs'>只能查询，不能创建或修改任何数据。适合分享给只需要查资料的工具。</span>
            </span>
          </label>
          {create.error ? <p className='text-destructive text-sm'>{create.error.message}</p> : null}
          <DialogFooter>
            <Button type='submit' disabled={create.isPending}>
              创建
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function CopyBlock({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className='grid gap-1.5'>
      <div className='flex items-center justify-between'>
        <Label>{label}</Label>
        <Button
          type='button'
          size='sm'
          variant='ghost'
          onClick={() => {
            void navigator.clipboard.writeText(value).then(() => {
              setCopied(true);
              setTimeout(() => setCopied(false), 1500);
            });
          }}
        >
          {copied ? <Icons.check /> : <Icons.copy />}
          {copied ? '已复制' : '复制'}
        </Button>
      </div>
      <pre className='bg-muted overflow-x-auto rounded-md p-3 font-mono text-xs leading-5 whitespace-pre-wrap break-all'>{value}</pre>
    </div>
  );
}

function CreatedKeyDialog({ created, onClose }: { created: CreatedApiKeyDto; onClose: () => void }) {
  const claudeCode = `claude mcp add --transport http ainative ${MCP_URL} --header "Authorization: Bearer ${created.token}"`;
  const json = JSON.stringify(
    { mcpServers: { ainative: { type: 'http', url: MCP_URL, headers: { Authorization: `Bearer ${created.token}` } } } },
    null,
    2,
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className='sm:max-w-2xl'>
        <DialogHeader>
          <DialogTitle>密钥「{created.key.name}」已创建</DialogTitle>
          <DialogDescription>密钥只显示这一次，关闭后无法再查看。请现在复制保存；丢失后吊销并重新创建即可。</DialogDescription>
        </DialogHeader>
        <div className='grid gap-4'>
          <CopyBlock label='API 密钥' value={created.token} />
          <CopyBlock label='接入 Claude Code（在终端运行）' value={claudeCode} />
          <CopyBlock label='其他 MCP 客户端（Streamable HTTP 配置）' value={json} />
        </div>
        <DialogFooter>
          <Button onClick={onClose}>我已保存</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
