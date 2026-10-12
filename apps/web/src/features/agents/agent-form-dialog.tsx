'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { AgentDto, SaveAgentRequest } from '@ainative/api-client';
import { FeatureChecklist } from '@/components/app/feature-checklist';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const NONE = '__none__';

export function AgentFormDialog({
  open,
  onOpenChange,
  agent,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agent?: AgentDto;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='max-h-[90svh] overflow-y-auto sm:max-w-xl'>
        <DialogHeader>
          <DialogTitle>{agent ? `编辑 ${agent.name}` : '招聘智能体'}</DialogTitle>
          <DialogDescription>智能体按岗位职责工作，权限决定它能调用哪些平台动作。</DialogDescription>
        </DialogHeader>
        {open ? <AgentForm agent={agent} onDone={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function AgentForm({ agent, onDone }: { agent?: AgentDto; onDone: () => void }) {
  const queryClient = useQueryClient();
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const models = useQuery({ queryKey: queryKeys.models, queryFn: ({ signal }) => api.models.catalog(signal), staleTime: Infinity });
  const actions = useQuery({ queryKey: queryKeys.actions, queryFn: ({ signal }) => api.actions.list(signal), staleTime: Infinity });

  const [form, setForm] = useState<SaveAgentRequest>(() => ({
    name: agent?.name ?? '',
    title: agent?.title ?? '',
    instructions: agent?.instructions ?? '',
    reportsToId: agent?.reportsToId,
    modelProvider: agent?.modelProvider,
    model: agent?.model,
    permissions: agent?.permissions ?? [],
    approvalRequiredActions: agent?.approvalRequiredActions ?? [],
    heartbeatIntervalSeconds: agent?.heartbeatIntervalSeconds ?? 0,
    monthlyBudgetUsd: agent?.monthlyBudgetUsd ?? 10,
  }));
  // 新建时默认不提交权限，由后端使用为智能体设定的默认权限；打开「自定义权限」后才只授予勾选的权限。
  // 编辑时始终提交（后端收到空值会重置为默认权限）。
  const [customPermissions, setCustomPermissions] = useState(!!agent);
  const set = <K extends keyof SaveAgentRequest>(key: K, value: SaveAgentRequest[K]) => setForm((f) => ({ ...f, [key]: value }));
  const toggleApproval = (value: string) =>
    setForm((f) => {
      const current = f.approvalRequiredActions ?? [];
      return { ...f, approvalRequiredActions: current.includes(value) ? current.filter((v) => v !== value) : [...current, value] };
    });

  const save = useMutation({
    mutationFn: (request: SaveAgentRequest) => (agent ? api.agents.update(agent.id, request) : api.agents.create(request)),
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.agents });
      toast.success(agent ? '已保存' : `${saved.name} 已加入团队`);
      onDone();
    },
  });

  const providers = models.data?.providers.filter((p) => p.configured) ?? [];
  const selectedProvider = form.modelProvider ?? models.data?.defaultProvider;
  const providerModels = providers.find((p) => p.name === selectedProvider)?.models ?? [];

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate({ ...form, permissions: customPermissions ? (form.permissions ?? []) : undefined });
      }}
    >
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='agent-name'>名字</Label>
          <Input id='agent-name' value={form.name} onChange={(e) => set('name', e.target.value)} required maxLength={100} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='agent-title'>岗位</Label>
          <Input id='agent-title' value={form.title} onChange={(e) => set('title', e.target.value)} required maxLength={100} placeholder='例如：市场研究员' />
        </div>
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='agent-instructions'>岗位职责与工作方式</Label>
        <Textarea
          id='agent-instructions'
          value={form.instructions}
          onChange={(e) => set('instructions', e.target.value)}
          rows={5}
          placeholder='描述它负责什么、如何判断完成、遇到问题找谁。'
        />
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label>汇报给</Label>
          <Select value={form.reportsToId ?? NONE} onValueChange={(v) => set('reportsToId', v === NONE ? undefined : v)}>
            <SelectTrigger className='w-full'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NONE}>无（最高层）</SelectItem>
              {agents.data
                ?.filter((a) => a.id !== agent?.id && a.status !== 'terminated')
                .map((a) => (
                  <SelectItem key={a.id} value={a.id}>
                    {a.name} · {a.title}
                  </SelectItem>
                ))}
            </SelectContent>
          </Select>
        </div>
        <div className='grid gap-2'>
          <Label>模型</Label>
          <Select
            value={selectedProvider && (form.model ?? models.data?.defaultModel) ? `${selectedProvider}|${form.model ?? models.data?.defaultModel}` : undefined}
            onValueChange={(v) => {
              const [provider, model] = v.split('|');
              setForm((f) => ({ ...f, modelProvider: provider, model }));
            }}
          >
            <SelectTrigger className='w-full'>
              <SelectValue placeholder='使用默认模型' />
            </SelectTrigger>
            <SelectContent>
              {providers.flatMap((p) =>
                p.models.map((m) => (
                  <SelectItem key={`${p.name}|${m}`} value={`${p.name}|${m}`}>
                    {m} <span className='text-muted-foreground'>· {p.name}</span>
                  </SelectItem>
                )),
              )}
            </SelectContent>
          </Select>
          {providerModels.length === 0 && models.data ? (
            <p className='text-muted-foreground text-xs'>当前提供方没有登记模型，将使用默认模型。</p>
          ) : null}
        </div>
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='agent-budget'>每月预算（美元）</Label>
          <Input
            id='agent-budget'
            type='number'
            min={0}
            step='0.01'
            value={form.monthlyBudgetUsd}
            onChange={(e) => set('monthlyBudgetUsd', Number(e.target.value))}
          />
          <p className='text-muted-foreground text-xs'>超出后自动暂停；0 表示不限制。</p>
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='agent-heartbeat'>定时心跳（秒）</Label>
          <Input
            id='agent-heartbeat'
            type='number'
            min={0}
            value={form.heartbeatIntervalSeconds}
            onChange={(e) => set('heartbeatIntervalSeconds', Number(e.target.value))}
          />
          <p className='text-muted-foreground text-xs'>0 表示只在有事时唤醒；否则 60 – 86400。</p>
        </div>
      </div>
      <fieldset className='grid gap-2'>
        <legend className='mb-2 text-sm font-medium'>权限</legend>
        {agent ? null : (
          <div className='flex items-start gap-3 rounded-md border p-3'>
            <Switch id='agent-custom-permissions' checked={customPermissions} onCheckedChange={setCustomPermissions} className='mt-0.5' />
            <div className='grid gap-1'>
              <Label htmlFor='agent-custom-permissions'>自定义权限</Label>
              <p className='text-muted-foreground text-xs'>
                {customPermissions
                  ? '只授予下面勾选的权限。'
                  : '使用平台为智能体设定的默认权限（团队协作、任务、知识库与记忆等常用能力）。招聘后可以在详情页查看，随时调整。'}
              </p>
            </div>
          </div>
        )}
        {customPermissions ? <FeatureChecklist value={form.permissions ?? []} onChange={(permissions) => set('permissions', permissions)} /> : null}
      </fieldset>
      <fieldset className='grid gap-2'>
        <legend className='mb-1 text-sm font-medium'>需要人工审批的动作</legend>
        <p className='text-muted-foreground mb-1 text-xs'>招聘智能体始终需要审批；这里可以额外要求其他动作也先经过你同意。</p>
        {actions.isPending ? <Skeleton className='h-16' /> : null}
        <div className='grid gap-2 sm:grid-cols-2'>
          {actions.data
            ?.filter((a) => a.isMutation && !a.requiresApprovalForAgents)
            .map((a) => (
              <label key={a.key} className='flex items-center gap-2 text-sm'>
                <input
                  type='checkbox'
                  className='accent-primary size-4'
                  checked={form.approvalRequiredActions?.includes(a.key) ?? false}
                  onChange={() => toggleApproval(a.key)}
                />
                {a.title}
              </label>
            ))}
        </div>
      </fieldset>
      <DialogFooter>
        <Button type='submit' disabled={save.isPending}>
          {agent ? '保存' : '招聘'}
        </Button>
      </DialogFooter>
    </form>
  );
}
