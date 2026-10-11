'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ProjectMilestoneDto } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/** milestone 为 null 时新建，否则编辑。 */
export function MilestoneFormDialog({
  open,
  projectId,
  milestone,
  onOpenChange,
}: {
  open: boolean;
  projectId: string;
  milestone?: ProjectMilestoneDto | null;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        {open ? <MilestoneForm key={milestone?.id ?? 'new'} projectId={projectId} milestone={milestone ?? null} onClose={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function MilestoneForm({ projectId, milestone, onClose }: { projectId: string; milestone: ProjectMilestoneDto | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(milestone?.name ?? '');
  const [description, setDescription] = useState(milestone?.description ?? '');
  const [dueDate, setDueDate] = useState(milestone?.dueDate ?? '');
  const save = useMutation({
    mutationFn: () => {
      const request = { name, description, dueDate: dueDate || undefined };
      return milestone ? api.projects.updateMilestone(projectId, milestone.id, request) : api.projects.addMilestone(projectId, request);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.project(projectId) });
      await queryClient.invalidateQueries({ queryKey: ['projects'] });
      onClose();
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{milestone ? '编辑里程碑' : '添加里程碑'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='milestone-name'>名称</Label>
        <Input id='milestone-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder='例如：完成设计稿' />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='milestone-due'>截止日期</Label>
        <Input id='milestone-due' type='date' value={dueDate} onChange={(e) => setDueDate(e.target.value)} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='milestone-description'>完成标准</Label>
        <Textarea id='milestone-description' value={description} onChange={(e) => setDescription(e.target.value)} rows={3} maxLength={2000} />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          保存
        </Button>
      </DialogFooter>
    </form>
  );
}
