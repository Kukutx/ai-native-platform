'use client';

import { useDeferredValue, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { WorkItemDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { Input } from '@/components/ui/input';
import { api } from '@/lib/api';
import { workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

/**
 * 按标题或描述搜索任务，选中后交给调用方（例如添加前置任务）。给了 projectId 时只在这个项目里找；exclude 里的任务不列出。
 */
export function TaskPicker({
  onPick,
  exclude,
  projectId,
  placeholder,
  disabled,
}: {
  onPick: (task: WorkItemDto) => void;
  exclude: string[];
  projectId?: string;
  placeholder?: string;
  disabled?: boolean;
}) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const term = useDeferredValue(search.trim());
  const results = useQuery({
    queryKey: queryKeys.workItems({ search: term, projectId, picker: true }),
    queryFn: async ({ signal }) =>
      (await api.workItems.list(undefined, undefined, undefined, term || undefined, false, 1, 10, projectId, undefined, undefined, undefined, signal)).items,
    enabled: open,
    placeholderData: keepPreviousData,
  });
  const options = results.data?.filter((t) => !exclude.includes(t.id)) ?? [];

  return (
    <div className='relative'>
      <Input
        value={search}
        disabled={disabled}
        onChange={(e) => {
          setSearch(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        // 延迟关闭，让点击结果先生效。
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        placeholder={placeholder ?? '搜索任务标题'}
        aria-label={placeholder ?? '搜索任务标题'}
      />
      {open ? (
        <div className='bg-popover absolute z-50 mt-1 max-h-64 w-full overflow-y-auto rounded-md border p-1 shadow-md'>
          {options.length ? (
            options.map((task) => (
              <button
                key={task.id}
                type='button'
                className='hover:bg-accent flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm'
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  onPick(task);
                  setSearch('');
                  setOpen(false);
                }}
              >
                <span className='text-muted-foreground shrink-0 font-mono text-xs'>{task.key}</span>
                <span className='min-w-0 flex-1 truncate'>{task.title}</span>
                <StatusBadge meta={workItemStatus[task.status]} />
              </button>
            ))
          ) : (
            <p className='text-muted-foreground px-2 py-1.5 text-sm'>{results.isFetching ? '搜索中…' : '没有找到任务'}</p>
          )}
        </div>
      ) : null}
    </div>
  );
}
