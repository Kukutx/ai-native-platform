'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { McpApprovalMode, McpServerDto, SaveMcpServerRequest } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { mcpApprovalMode } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { fieldError, fieldErrorsOf, FieldMessage } from '@/components/app/field-errors';

interface HeaderRow {
  uid: number;
  name: string;
  value: string;
}

let uid = 0;
const nextUid = () => ++uid;

const approvalModes = Object.keys(mcpApprovalMode) as McpApprovalMode[];

/**
 * 新建或编辑外部 MCP 服务。保存时平台会连接服务读取工具列表；连不上也会保存，之后可以再刷新。
 */
export function McpServerFormDialog({
  open,
  server,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  server?: McpServerDto;
  onOpenChange: (open: boolean) => void;
  onSaved?: (server: McpServerDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='max-h-[90vh] overflow-y-auto sm:max-w-2xl'>
        {open ? <McpServerForm server={server} onClose={() => onOpenChange(false)} onSaved={onSaved} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function McpServerForm({ server, onClose, onSaved }: { server?: McpServerDto; onClose: () => void; onSaved?: (server: McpServerDto) => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(server?.name ?? '');
  const [key, setKey] = useState(server?.key ?? '');
  const [url, setUrl] = useState(server?.url ?? '');
  const [description, setDescription] = useState(server?.description ?? '');
  const [approval, setApproval] = useState<McpApprovalMode>(server?.approvalMode ?? 'writes_only');
  const [enabled, setEnabled] = useState(server?.isEnabled ?? true);
  const [headers, setHeaders] = useState<HeaderRow[]>(() => (server?.headers ?? []).map((h) => ({ uid: nextUid(), ...h })));
  const save = useMutation({
    mutationFn: () => {
      const request: SaveMcpServerRequest = {
        key: key.trim(),
        name: name.trim(),
        description: description.trim(),
        url: url.trim(),
        headers: headers.filter((h) => h.name.trim() || h.value.trim()).map((h) => ({ name: h.name.trim(), value: h.value })),
        isEnabled: enabled,
        approvalMode: approval,
      };
      return server ? api.integrations.updateMcpServer(server.id, request) : api.integrations.createMcpServer(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['integrations'] });
      await queryClient.invalidateQueries({ queryKey: queryKeys.actions });
      if (saved.lastError && !saved.toolsRefreshedAt) {
        toast.warning(`已保存，但暂时连不上服务：${saved.lastError}`);
      } else if (saved.lastError) {
        toast.warning(`已保存，但刷新工具失败，沿用上次的工具列表：${saved.lastError}`);
      } else {
        toast.success(`已保存，读取到 ${saved.tools.length} 个工具`);
      }

      onSaved?.(saved);
      onClose();
    },
  });
  const errors = fieldErrorsOf(save.error);
  const updateHeader = (row: number, patch: Partial<HeaderRow>) => setHeaders((list) => list.map((h) => (h.uid === row ? { ...h, ...patch } : h)));

  return (
    <form
      className='grid gap-4'
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{server ? `编辑「${server.name}」` : '接入外部服务'}</DialogTitle>
        <DialogDescription>填写支持 MCP（Streamable HTTP）的服务地址。保存时会连接服务读取工具列表，之后智能体、对话和工作流都能使用这些工具。</DialogDescription>
      </DialogHeader>
      <div className='grid items-start gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='mcp-name'>名称</Label>
          <Input id='mcp-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder='例如 GitHub' />
          <FieldMessage message={fieldError(errors, 'name')} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='mcp-key'>标识</Label>
          <Input
            id='mcp-key'
            className='font-mono'
            value={key}
            onChange={(e) => setKey(e.target.value.toLowerCase())}
            required
            maxLength={20}
            pattern='[a-z][a-z0-9]{1,19}'
            title='2 到 20 个小写字母或数字，以字母开头'
            placeholder='github'
          />
          <FieldMessage message={fieldError(errors, 'key')} />
          {fieldError(errors, 'key') ? null : (
            <p className='text-muted-foreground text-xs'>
              工具的动作标识是 <code className='font-mono whitespace-nowrap'>mcp.{key || '标识'}.工具名</code>，工作流按它引用。
            </p>
          )}
        </div>
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='mcp-url'>服务地址</Label>
        <Input id='mcp-url' className='font-mono' type='url' value={url} onChange={(e) => setUrl(e.target.value)} required maxLength={2000} placeholder='https://example.com/mcp' />
        <FieldMessage message={fieldError(errors, 'url')} />
      </div>
      <div className='grid gap-2'>
        <Label>请求头</Label>
        {headers.map((header) => (
          <div key={header.uid} className='flex gap-2'>
            <Input
              aria-label='请求头名称'
              className='w-40 shrink-0 font-mono sm:w-48'
              value={header.name}
              placeholder='Authorization'
              onChange={(e) => updateHeader(header.uid, { name: e.target.value })}
            />
            <Input
              aria-label='请求头的值'
              className='min-w-0 font-mono'
              value={header.value}
              placeholder='Bearer {{ secrets.GITHUB_TOKEN }}'
              onChange={(e) => updateHeader(header.uid, { value: e.target.value })}
            />
            <Button type='button' size='icon' variant='ghost' aria-label='删除请求头' onClick={() => setHeaders((list) => list.filter((h) => h.uid !== header.uid))}>
              <Icons.close className='size-4' />
            </Button>
          </div>
        ))}
        <div>
          <Button type='button' variant='ghost' size='sm' className='-ml-2' onClick={() => setHeaders((list) => [...list, { uid: nextUid(), name: '', value: '' }])}>
            <Icons.add className='size-4' />
            添加请求头
          </Button>
        </div>
        <p className='text-muted-foreground text-xs'>
          令牌请先存进「密钥」，再写成 <code className='font-mono whitespace-nowrap'>{'Bearer {{ secrets.名称 }}'}</code>。密钥只在连接服务时解密，页面上和返回结果里都看不到明文。
        </p>
        <FieldMessage message={fieldError(errors, 'headers')} />
      </div>
      <div className='grid gap-2'>
        <Label>智能体调用时</Label>
        <Select value={approval} onValueChange={(v) => setApproval(v as McpApprovalMode)}>
          <SelectTrigger className='w-full'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {approvalModes.map((mode) => (
              <SelectItem key={mode} value={mode}>
                {mcpApprovalMode[mode].label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <p className='text-muted-foreground text-xs'>{mcpApprovalMode[approval].hint}成员在界面和对话里调用时不需要审批。</p>
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='mcp-description'>说明</Label>
        <Textarea id='mcp-description' value={description} onChange={(e) => setDescription(e.target.value)} rows={2} maxLength={1000} placeholder='这个服务用来做什么（可选）' />
        <FieldMessage message={fieldError(errors, 'description')} />
      </div>
      <label className='flex items-center justify-between gap-4 rounded-md border p-3 text-sm'>
        <span>
          <span className='font-medium'>启用</span>
          <span className='text-muted-foreground block text-xs'>停用后它的工具不再出现在动作列表里，引用它的工作流步骤会失败。</span>
        </span>
        <Switch checked={enabled} onCheckedChange={setEnabled} />
      </label>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          {save.isPending ? <Icons.spinner className='animate-spin' /> : null}
          {save.isPending ? '正在连接…' : server ? '保存' : '接入'}
        </Button>
      </DialogFooter>
    </form>
  );
}
