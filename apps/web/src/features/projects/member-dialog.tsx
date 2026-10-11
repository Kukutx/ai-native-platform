'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { ActorSelect, type ActorRef } from './actor-select';

/** 添加项目成员；exclude 是已在项目里的人（user:&lt;id&gt; / agent:&lt;id&gt;）。 */
export function MemberDialog({ open, projectId, exclude, onOpenChange }: { open: boolean; projectId: string; exclude: string[]; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-md'>{open ? <MemberForm projectId={projectId} exclude={exclude} onClose={() => onOpenChange(false)} /> : null}</DialogContent>
    </Dialog>
  );
}

function MemberForm({ projectId, exclude, onClose }: { projectId: string; exclude: string[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [member, setMember] = useState<ActorRef>({});
  const [responsibility, setResponsibility] = useState('');
  const add = useMutation({
    mutationFn: () => api.projects.addMember(projectId, { userId: member.userId, agentId: member.agentId, responsibility }),
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
        add.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>添加成员</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='project-member'>成员或智能体</Label>
        <ActorSelect id='project-member' value={member} onChange={setMember} exclude={exclude} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='project-responsibility'>职责</Label>
        <Input id='project-responsibility' value={responsibility} onChange={(e) => setResponsibility(e.target.value)} maxLength={100} placeholder='例如：前端开发、测试' />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={add.isPending || (!member.userId && !member.agentId)}>
          添加
        </Button>
      </DialogFooter>
    </form>
  );
}
