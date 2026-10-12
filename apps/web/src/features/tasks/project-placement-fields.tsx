'use client';

import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const NONE = '__none__';

export type Placement = { projectId?: string; milestoneId?: string };

/**
 * 任务所属的项目和里程碑。项目列表只列未结束的（当前所在的项目除外），里程碑只列未完成的（当前所在的除外）。
 * 没有项目查看权限时不显示。
 */
export function ProjectPlacementFields({ value, onChange, disabled }: { value: Placement; onChange: (value: Placement) => void; disabled?: boolean }) {
  const { can } = useSession();
  const visible = can('projects.view');
  const projects = useQuery({
    queryKey: queryKeys.projects({ open: true, picker: true }),
    queryFn: ({ signal }) => api.projects.list(undefined, undefined, true, 1, 200, signal),
    enabled: visible,
  });
  const project = useQuery({
    queryKey: queryKeys.project(value.projectId ?? ''),
    queryFn: ({ signal }) => api.projects.get(value.projectId!, signal),
    enabled: visible && !!value.projectId,
  });
  if (!visible) {
    return null;
  }

  const options = projects.data?.items ?? [];
  const current = project.data?.project;
  const milestones = project.data?.milestones.filter((m) => !m.completedAt || m.id === value.milestoneId) ?? [];

  return (
    <>
      <div className='grid gap-2'>
        <Label>项目</Label>
        <Select value={value.projectId ?? NONE} disabled={disabled} onValueChange={(v) => onChange({ projectId: v === NONE ? undefined : v })}>
          <SelectTrigger className='w-full'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={NONE}>不属于项目</SelectItem>
            {current && !options.some((p) => p.id === current.id) ? <SelectItem value={current.id}>{current.name}</SelectItem> : null}
            {options.map((p) => (
              <SelectItem key={p.id} value={p.id}>
                {p.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      <div className='grid gap-2'>
        <Label>里程碑</Label>
        <Select
          value={value.milestoneId ?? NONE}
          disabled={disabled || !value.projectId}
          onValueChange={(v) => onChange({ projectId: value.projectId, milestoneId: v === NONE ? undefined : v })}
        >
          <SelectTrigger className='w-full'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={NONE}>不属于里程碑</SelectItem>
            {milestones.map((m) => (
              <SelectItem key={m.id} value={m.id}>
                {m.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
    </>
  );
}
