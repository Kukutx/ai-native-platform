'use client';

import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from '@/components/ui/select';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const NOBODY = '__none__';

/** 成员或智能体二选一；值编码为 user:&lt;id&gt; / agent:&lt;id&gt;。 */
export type ActorRef = { userId?: string; agentId?: string };

export function encodeActor(ref: ActorRef) {
  return ref.userId ? `user:${ref.userId}` : ref.agentId ? `agent:${ref.agentId}` : NOBODY;
}

function decodeActor(value: string): ActorRef {
  const [kind, id] = value.split(':');
  return kind === 'user' ? { userId: id } : kind === 'agent' ? { agentId: id } : {};
}

/**
 * 从成员和智能体里选一位。看不到成员名单或智能体名单时对应分组不出现。
 * exclude 里的人不出现在选项里（例如已经是项目成员的）。
 */
export function ActorSelect({
  value,
  onChange,
  noneLabel,
  exclude = [],
  id,
  disabled,
}: {
  value: ActorRef;
  onChange: (value: ActorRef) => void;
  noneLabel?: string;
  exclude?: string[];
  id?: string;
  disabled?: boolean;
}) {
  const { can } = useSession();
  const members = useQuery({ queryKey: queryKeys.members, queryFn: ({ signal }) => api.members.list(signal), enabled: can('platform.members.view') });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal), enabled: can('agents.view') });
  const current = encodeActor(value);
  const people = members.data?.filter((m) => (m.status === 'active' && !exclude.includes(`user:${m.userId}`)) || `user:${m.userId}` === current) ?? [];
  const bots = agents.data?.filter((a) => (a.status !== 'terminated' && !exclude.includes(`agent:${a.id}`)) || `agent:${a.id}` === current) ?? [];
  // 当前选中的人不在可见名单里（没有查看名单的权限，或者已被移除）：保留一项，不让下拉框显示成空白。
  const unlisted =
    current !== NOBODY && !members.isLoading && !agents.isLoading && !people.some((m) => `user:${m.userId}` === current) && !bots.some((a) => `agent:${a.id}` === current);

  return (
    <Select value={current} onValueChange={(v) => onChange(decodeActor(v))} disabled={disabled}>
      <SelectTrigger id={id} className='w-full'>
        <SelectValue placeholder='请选择' />
      </SelectTrigger>
      <SelectContent>
        {noneLabel ? <SelectItem value={NOBODY}>{noneLabel}</SelectItem> : null}
        {unlisted ? <SelectItem value={current}>{value.userId ? '其他成员' : '其他智能体'}</SelectItem> : null}
        {people.length ? (
          <SelectGroup>
            <SelectLabel>成员</SelectLabel>
            {people.map((m) => (
              <SelectItem key={m.userId} value={`user:${m.userId}`}>
                {m.displayName}
              </SelectItem>
            ))}
          </SelectGroup>
        ) : null}
        {bots.length ? (
          <SelectGroup>
            <SelectLabel>智能体</SelectLabel>
            {bots.map((a) => (
              <SelectItem key={a.id} value={`agent:${a.id}`}>
                {a.name} · {a.title}
              </SelectItem>
            ))}
          </SelectGroup>
        ) : null}
      </SelectContent>
    </Select>
  );
}
