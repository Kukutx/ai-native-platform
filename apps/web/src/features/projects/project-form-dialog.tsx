'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ProjectDto, ProjectStatus } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { projectStatus } from '@/lib/labels';
import { ActorSelect, type ActorRef } from './actor-select';

/** project 为 null 时新建，否则编辑。 */
export function ProjectFormDialog({
  open,
  project,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  project?: ProjectDto | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (projectId: string) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-xl'>
        {open ? <ProjectForm key={project?.id ?? 'new'} project={project ?? null} onClose={() => onOpenChange(false)} onSaved={onSaved} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function ProjectForm({ project, onClose, onSaved }: { project: ProjectDto | null; onClose: () => void; onSaved?: (projectId: string) => void }) {
  const queryClient = useQueryClient();
  const { session } = useSession();
  const [name, setName] = useState(project?.name ?? '');
  const [description, setDescription] = useState(project?.description ?? '');
  const [status, setStatus] = useState<ProjectStatus>(project?.status ?? 'active');
  const [lead, setLead] = useState<ActorRef>(
    project
      ? project.lead?.kind === 'agent'
        ? { agentId: project.lead.id }
        : project.lead?.kind === 'user'
          ? { userId: project.lead.id }
          : {}
      : { userId: session?.user.id },
  );
  const [startDate, setStartDate] = useState(project?.startDate ?? '');
  const [targetDate, setTargetDate] = useState(project?.targetDate ?? '');
  const save = useMutation({
    mutationFn: async () => {
      const request = {
        name,
        description,
        status,
        leadUserId: lead.userId,
        leadAgentId: lead.agentId,
        startDate: startDate || undefined,
        targetDate: targetDate || undefined,
        expectedUpdatedAt: project?.updatedAt,
      };
      return project ? (await api.projects.update(project.id, request)).id : (await api.projects.create(request)).project.id;
    },
    onSuccess: async (id) => {
      await queryClient.invalidateQueries({ queryKey: ['projects'] });
      await queryClient.invalidateQueries({ queryKey: ['project', id] });
      onSaved?.(id);
      onClose();
    },
    // 冲突时刷新，下次打开就是最新内容。
    onError: () => queryClient.invalidateQueries({ queryKey: ['project'] }),
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
        <DialogTitle>{project ? '编辑项目' : '新建项目'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='project-name'>名称</Label>
        <Input id='project-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder='例如：官网改版' />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='project-description'>目标与说明</Label>
        <Textarea
          id='project-description'
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={4}
          maxLength={10000}
          placeholder='要达成什么、范围和验收标准。智能体负责人会据此规划任务。'
        />
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='project-lead'>负责人</Label>
          <ActorSelect id='project-lead' value={lead} onChange={setLead} noneLabel='暂无负责人' />
        </div>
        <div className='grid gap-2'>
          <Label>状态</Label>
          <Select value={status} onValueChange={(v) => setStatus(v as ProjectStatus)}>
            <SelectTrigger className='w-full'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {Object.entries(projectStatus).map(([key, meta]) => (
                <SelectItem key={key} value={key}>
                  {meta.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='project-start'>开始日期</Label>
          <Input id='project-start' type='date' value={startDate} onChange={(e) => setStartDate(e.target.value)} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='project-target'>目标日期</Label>
          <Input id='project-target' type='date' value={targetDate} min={startDate || undefined} onChange={(e) => setTargetDate(e.target.value)} />
        </div>
      </div>
      {lead.agentId ? <p className='text-muted-foreground text-xs'>智能体负责人会被唤醒，按目标规划里程碑和任务；任务受阻或里程碑完成时也会通知它。</p> : null}
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
