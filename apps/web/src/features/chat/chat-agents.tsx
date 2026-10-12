'use client';

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { AgentDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectSeparator, SelectTrigger, SelectValue } from '@/components/ui/select';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/** 不指定智能体时的对话对象：平台的通用助理，用默认模型。 */
export const DEFAULT_ASSISTANT = '默认助理';
const DEFAULT = '__default__';

/**
 * 可以对话的智能体（已终止的除外）以及按编号取名称的方法。没有查看智能体的权限时列表为空，
 * 只能和默认助理对话；看不到名称的智能体显示为「智能体」。
 */
export function useChatAgents() {
  const { can } = useSession();
  const visible = can('agents.view');
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal), enabled: visible });

  return useMemo(() => {
    const byId = new Map(agents.data?.map((a) => [a.id, a]));
    return {
      visible,
      available: agents.data?.filter((a) => a.status !== 'terminated') ?? [],
      find: (id?: string | null): AgentDto | undefined => (id ? byId.get(id) : undefined),
      label: (id?: string | null) => (id ? (byId.get(id)?.name ?? '智能体') : DEFAULT_ASSISTANT),
    };
  }, [agents.data, visible]);
}

/** 新对话选择对话对象：默认助理或某个智能体（以它的岗位设定和模型回答，操作仍按你的权限执行）。 */
export function ChatAgentSelect({ value, onChange, agents }: { value?: string; onChange: (agentId: string | undefined) => void; agents: AgentDto[] }) {
  // 地址里的智能体已不在列表里（例如已终止）时按默认助理处理。
  const current = value && agents.some((a) => a.id === value) ? value : DEFAULT;

  return (
    <Select value={current} onValueChange={(v) => onChange(v === DEFAULT ? undefined : v)}>
      <SelectTrigger size='sm' className='w-44 max-w-[50vw]' aria-label='对话对象'>
        <SelectValue />
      </SelectTrigger>
      <SelectContent align='end'>
        <SelectItem value={DEFAULT}>{DEFAULT_ASSISTANT}</SelectItem>
        {agents.length ? (
          <>
            <SelectSeparator />
            <SelectGroup>
              <SelectLabel>智能体</SelectLabel>
              {agents.map((a) => (
                <SelectItem key={a.id} value={a.id}>
                  {a.name}
                  <span className='text-muted-foreground text-xs'>{a.title}</span>
                </SelectItem>
              ))}
            </SelectGroup>
          </>
        ) : null}
      </SelectContent>
    </Select>
  );
}
