'use client';

import { useQuery } from '@tanstack/react-query';
import type { AgentDto, MemberDto, WorkItemDto, WorkItemPriority } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectSeparator, SelectTrigger, SelectValue } from '@/components/ui/select';
import { pickOption, useSearchText } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { priority as priorityLabels } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const ALL = '__all__';

/** 任务页记在地址里的状态：视图、筛选条件和列表页码。 */
export const taskFilterDefaults = { view: 'board', q: '', assignee: '', project: '', priority: '', page: 1 };

export type TaskFilterState = { [K in keyof typeof taskFilterDefaults]: (typeof taskFilterDefaults)[K] extends number ? number : string };

/**
 * 负责人筛选：me（我负责的）、none（未指派）、user:&lt;id&gt;、agent:&lt;id&gt;。
 * 后端只支持按智能体筛选，其余在已加载的任务里筛选。
 */
export interface AssigneeFilter {
  agentId?: string;
  userId?: string;
  none?: boolean;
}

export function parseAssignee(value: string, me: string | undefined): AssigneeFilter | undefined {
  if (value === 'me') {
    return me ? { userId: me } : undefined;
  }

  if (value === 'none') {
    return { none: true };
  }

  const [kind, id] = value.split(':');
  if (!id) {
    return undefined;
  }

  return kind === 'agent' ? { agentId: id } : kind === 'user' ? { userId: id } : undefined;
}

/** 后端不支持、需要在浏览器里筛选的条件：优先级、指派给成员、未指派。 */
export function clientFilter(filters: { assignee?: AssigneeFilter; priority?: WorkItemPriority }) {
  const { assignee, priority } = filters;
  if (!priority && !assignee?.userId && !assignee?.none) {
    return undefined;
  }

  return (task: WorkItemDto) =>
    (!priority || task.priority === priority) &&
    (!assignee?.userId || task.assigneeUserId === assignee.userId) &&
    (!assignee?.none || (!task.assigneeUserId && !task.assigneeAgentId));
}

export function priorityFilter(value: string): WorkItemPriority | undefined {
  return pickOption(value, priorityLabels);
}

/** 任务看板和列表共用的筛选栏：关键词、负责人、项目、优先级。 */
export function TaskFilterBar({
  filters,
  onChange,
  members,
  agents,
}: {
  filters: TaskFilterState;
  onChange: (patch: Partial<TaskFilterState>) => void;
  members?: MemberDto[];
  agents?: AgentDto[];
}) {
  const { can } = useSession();
  const [search, setSearch] = useSearchText(filters.q, (q) => onChange({ q, page: 1 }));
  const projects = useQuery({
    queryKey: queryKeys.projects({ open: true, picker: true }),
    queryFn: ({ signal }) => api.projects.list(undefined, undefined, true, 1, 200, signal),
    enabled: can('projects.view'),
  });
  const people = members?.filter((m) => m.status === 'active' || `user:${m.userId}` === filters.assignee) ?? [];
  const bots = agents?.filter((a) => a.status !== 'terminated' || `agent:${a.id}` === filters.assignee) ?? [];
  const active = !!(filters.q || filters.assignee || filters.project || filters.priority);
  // 地址里的负责人不在名单里（例如已被移除）时也保留一项，下拉框不至于显示空白。
  const knownAssignee =
    !filters.assignee ||
    filters.assignee === 'me' ||
    filters.assignee === 'none' ||
    people.some((m) => `user:${m.userId}` === filters.assignee) ||
    bots.some((a) => `agent:${a.id}` === filters.assignee);
  const projectOptions = projects.data?.items ?? [];

  return (
    <div className='flex flex-wrap items-center gap-2' role='search' aria-label='筛选任务'>
      <div className='relative'>
        <Icons.search className='text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2' />
        <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder='搜索标题、描述或编号' className='w-56 pl-8' aria-label='搜索任务' />
      </div>
      <Select value={filters.assignee || ALL} onValueChange={(v) => onChange({ assignee: v === ALL ? '' : v, page: 1 })}>
        <SelectTrigger className='w-36' aria-label='按负责人筛选'>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={ALL}>全部负责人</SelectItem>
          <SelectItem value='me'>我负责的</SelectItem>
          <SelectItem value='none'>未指派</SelectItem>
          {knownAssignee ? null : <SelectItem value={filters.assignee}>{filters.assignee.startsWith('agent:') ? '智能体' : '成员'}</SelectItem>}
          {people.length ? (
            <>
              <SelectSeparator />
              <SelectGroup>
                <SelectLabel>成员</SelectLabel>
                {people.map((m) => (
                  <SelectItem key={m.userId} value={`user:${m.userId}`}>
                    {m.displayName}
                  </SelectItem>
                ))}
              </SelectGroup>
            </>
          ) : null}
          {bots.length ? (
            <>
              <SelectSeparator />
              <SelectGroup>
                <SelectLabel>智能体</SelectLabel>
                {bots.map((a) => (
                  <SelectItem key={a.id} value={`agent:${a.id}`}>
                    {a.name}
                  </SelectItem>
                ))}
              </SelectGroup>
            </>
          ) : null}
        </SelectContent>
      </Select>
      {can('projects.view') && (projectOptions.length || filters.project) ? (
        <Select value={filters.project || ALL} onValueChange={(v) => onChange({ project: v === ALL ? '' : v, page: 1 })}>
          <SelectTrigger className='w-40' aria-label='按项目筛选'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>全部项目</SelectItem>
            {filters.project && !projectOptions.some((p) => p.id === filters.project) ? <SelectItem value={filters.project}>所选项目</SelectItem> : null}
            {projectOptions.map((p) => (
              <SelectItem key={p.id} value={p.id}>
                {p.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      ) : null}
      <Select value={priorityFilter(filters.priority) ?? ALL} onValueChange={(v) => onChange({ priority: v === ALL ? '' : v, page: 1 })}>
        <SelectTrigger className='w-32' aria-label='按优先级筛选'>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={ALL}>全部优先级</SelectItem>
          {(Object.keys(priorityLabels) as WorkItemPriority[]).reverse().map((key) => (
            <SelectItem key={key} value={key}>
              优先级{priorityLabels[key]}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      {active ? (
        <Button variant='ghost' size='sm' onClick={() => onChange({ q: '', assignee: '', project: '', priority: '', page: 1 })}>
          <Icons.close />
          清除筛选
        </Button>
      ) : null}
    </div>
  );
}
