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
import { ProjectPlacementFields } from './project-placement-fields';

const UNASSIGNED = '__none__';

/**
 * 新建任务。parentId 为子任务（项目和里程碑沿用上级任务）；projectId / milestoneId 给定时直接放进该项目或里程碑。
 */
export function TaskCreateDialog({
  open,
  onOpenChange,
  parentId,
  projectId,
  milestoneId,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  parentId?: string;
  projectId?: string;
  milestoneId?: string;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        {open ? (
          <TaskForm key={`${parentId}-${projectId}-${milestoneId}`} parentId={parentId} projectId={projectId} milestoneId={milestoneId} onClose={() => onOpenChange(false)} />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function TaskForm({ parentId, projectId, milestoneId, onClose }: { parentId?: string; projectId?: string; milestoneId?: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });
  const [form, setForm] = useState<CreateWorkItemRequest>({ title: '', description: '', priority: 'medium', projectId, milestoneId });
  const fixedPlacement = !!parentId || !!projectId;
  const create = useMutation({
    mutationFn: () => api.workItems.create({ ...form, parentId, dueDate: form.dueDate || undefined }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      if (parentId) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.workItem(parentId) });
      }

      if (form.projectId) {
        await queryClient.invalidateQueries({ queryKey: ['projects'] });
        await queryClient.invalidateQueries({ queryKey: queryKeys.project(form.projectId) });
      }

      onClose();
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        create.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{parentId ? '新建子任务' : '新建任务'}</DialogTitle>
      </DialogHeader>
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
          <Select value={form.assigneeAgentId ?? UNASSIGNED} onValueChange={(v) => setForm({ ...form, assigneeAgentId: v === UNASSIGNED ? undefined : v })}>
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
        {fixedPlacement ? null : (
          <ProjectPlacementFields
            value={{ projectId: form.projectId, milestoneId: form.milestoneId }}
            onChange={(placement) => setForm({ ...form, ...placement })}
          />
        )}
        <div className='grid gap-2'>
          <Label htmlFor='task-due'>截止日期</Label>
          <Input id='task-due' type='date' value={form.dueDate ?? ''} onChange={(e) => setForm({ ...form, dueDate: e.target.value || undefined })} />
        </div>
      </div>
      <p className='text-muted-foreground text-xs'>
        {parentId ? '子任务跟上级任务在同一个项目和里程碑里。' : null}指派后智能体会立即被唤醒并开始处理。
      </p>
      <DialogFooter>
        <Button type='submit' disabled={create.isPending}>
          创建
        </Button>
      </DialogFooter>
    </form>
  );
}
