'use client';

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/** 界面上显示中文名称，悬停时给出原始键（动作键、权限键等），方便对照文档和排查问题。 */
export function RawKeyTooltip({ value, children }: { value: string; children: React.ReactElement }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>{children}</TooltipTrigger>
      <TooltipContent className='font-mono'>{value}</TooltipContent>
    </Tooltip>
  );
}

/**
 * 动作键 → 动作名称。目录只包含当前身份能使用的动作，智能体调用了你看不到的动作时返回 undefined，由调用方退回显示原始键。
 */
export function useActionTitles() {
  const actions = useQuery({ queryKey: queryKeys.actions, queryFn: ({ signal }) => api.actions.list(signal), staleTime: Infinity });
  return useMemo(() => {
    const titles = new Map(actions.data?.map((a) => [a.key, a.title]));
    return (key: string) => titles.get(key);
  }, [actions.data]);
}
