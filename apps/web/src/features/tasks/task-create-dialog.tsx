'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CreateWorkItemRequest, WorkItemPriority } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { priority } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const UNASSIGNED = '__none__';

export function TaskCreateDialog({ open, onOpenChange, parentId }: { open: boolean; onOpenChange: (open: boolean) => void; parentId?: string }) {
  const queryClient = useQueryClient();
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal), enabled: open });
  const [form, setForm] = useState<CreateWorkItemRequest>({ title: '', description: '', priority: 'medium' });
  const create = useMutation({
    mutationFn: () => api.workItems.create({ ...form, parentId }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      if (parentId) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.workItem(parentId) });
      }

      setForm({ title: '', description: '', priority: 'medium' });
      onOpenChange(false);
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        <DialogHeader>
          <DialogTitle>{parentId ? '新建子任务' : '新建任务'}</DialogTitle>
        </DialogHeader>
        <form
          className='grid gap-4'
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate();
          }}
        >
          <div className='grid gap-2'>
            <Label htmlFor='task-title'>标题</Label>
            <Input id='task-title' value={form.title} onChange={(e) => setForm({ ...form, title: e.target.value })} required maxLength={300} />
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='task-description'>描述</Label>
            <Textarea id='task-description' value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} rows={4} />
          </div>
          <div className='grid gap-4 sm:grid-cols-2'>
            <div className='grid gap-2'>
              <Label>优先级</Label>
              <Select value={form.priority} onValueChange={(v) => setForm({ ...form, priority: v as WorkItemPriority })}>
                <SelectTrigger className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {Object.entries(priority).map(([key, label]) => (
                    <SelectItem key={key} value={key}>
                      {label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className='grid gap-2'>
              <Label>指派给智能体</Label>
              <Select
                value={form.assigneeAgentId ?? UNASSIGNED}
                onValueChange={(v) => setForm({ ...form, assigneeAgentId: v === UNASSIGNED ? undefined : v })}
              >
                <SelectTrigger className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={UNASSIGNED}>暂不指派</SelectItem>
                  {agents.data
                    ?.filter((a) => a.status !== 'terminated')
                    .map((a) => (
                      <SelectItem key={a.id} value={a.id}>
                        {a.name} · {a.title}
                      </SelectItem>
                    ))}
                </SelectContent>
              </Select>
            </div>
          </div>
          <p className='text-muted-foreground text-xs'>指派后智能体会立即被唤醒并开始处理。</p>
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
