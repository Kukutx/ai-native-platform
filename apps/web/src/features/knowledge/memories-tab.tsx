'use client';

import { useState } from 'react';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import type { MemoryDto, MemoryScope } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { EmptyState } from '@/components/app/empty-state';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { pickOption, useSearchText, useUrlState } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { formatTime, memoryScope } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const ALL = 'all';
const PAGE_SIZE = 50;

export function MemoriesTab() {
  const { can, session } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const manage = can('memory.manage');
  // 筛选条件记在地址里（mem、scope）。
  const [urlFilters, setUrlFilters] = useUrlState({ mem: '', scope: '' });
  const term = urlFilters.mem;
  const scope: MemoryScope | typeof ALL = pickOption(urlFilters.scope, memoryScope) ?? ALL;
  const [filter, setFilter] = useSearchText(term, (mem) => setUrlFilters({ mem }));
  const [content, setContent] = useState('');
  const [shared, setShared] = useState(false);
  const filters = { scope, search: term };
  // 记忆会持续积累，按页加载，滚到底部点"加载更多"。
  const memories = useInfiniteQuery({
    queryKey: queryKeys.memories(filters),
    queryFn: ({ pageParam, signal }) => api.memories.list(scope === ALL ? undefined : scope, undefined, term || undefined, pageParam, PAGE_SIZE, signal),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.total ? last.page + 1 : undefined),
  });
  const items = memories.data?.pages.flatMap((p) => p.items) ?? [];
  const total = memories.data?.pages[0]?.total;
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['memories'] });
  const create = useMutation({
    mutationFn: (text: string) => api.memories.create({ content: text, shared, tags: [] }),
    onSuccess: async (_, submitted) => {
      // 保存期间又输入了新内容时保留新内容。
      setContent((current) => (current === submitted ? '' : current));
      await refresh();
    },
  });
  const remove = useMutation({ mutationFn: (id: string) => api.memories.delete(id), onSuccess: refresh });
  const canDelete = (m: MemoryDto) =>
    manage || (m.scope === 'tenant' ? m.createdBy.kind === 'user' && m.createdBy.id === session?.user.id : m.scope === 'user');

  return (
    <div className='space-y-4'>
      <form
        className='bg-muted/40 grid gap-2 rounded-xl border p-3'
        onSubmit={(e) => {
          e.preventDefault();
          create.mutate(content);
        }}
      >
        <Textarea
          value={content}
          onChange={(e) => setContent(e.target.value)}
          placeholder='记下一条值得长期记住的事实、偏好或约定，例如：周报每周五下午五点前提交。'
          rows={2}
          maxLength={2000}
          required
        />
        <div className='flex items-center justify-between gap-2'>
          <label className='flex items-center gap-2 text-sm'>
            <input type='checkbox' className='accent-primary' checked={shared} onChange={(e) => setShared(e.target.checked)} />
            共享给整个空间（智能体和其他成员也能回忆到）
          </label>
          <Button type='submit' size='sm' disabled={create.isPending || !content.trim()}>
            <Icons.memory />
            记住
          </Button>
        </div>
      </form>
      <div className='flex flex-wrap items-center gap-2'>
        <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder='搜索记忆' className='w-56' aria-label='搜索记忆' />
        <Select value={scope} onValueChange={(v) => setUrlFilters({ scope: v === ALL ? '' : v })}>
          <SelectTrigger className='w-36' aria-label='记忆范围'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>全部</SelectItem>
            <SelectItem value='tenant'>共享记忆</SelectItem>
            <SelectItem value='user'>{manage ? '成员记忆' : '我的记忆'}</SelectItem>
            {manage ? <SelectItem value='agent'>智能体记忆</SelectItem> : null}
          </SelectContent>
        </Select>
        {total !== undefined ? <span className='text-muted-foreground text-sm'>共 {total} 条</span> : null}
      </div>
      {memories.isPending ? (
        <Skeleton className='h-40 w-full' />
      ) : memories.isError && !memories.data ? (
        <ErrorState query={memories} />
      ) : items.length ? (
        <ul className='divide-y rounded-xl border'>
          {items.map((m) => (
            <li key={m.id} className='flex items-start gap-3 p-3'>
              <Badge variant={memoryScope[m.scope].tone} className='mt-0.5 shrink-0'>
                {memoryScope[m.scope].label}
              </Badge>
              <div className='min-w-0 flex-1 space-y-1'>
                <p className='text-sm whitespace-pre-wrap'>{m.content}</p>
                <p className='text-muted-foreground text-xs'>
                  {m.owner && m.owner.id !== m.createdBy.id ? `属于 ${m.owner.name} · ` : null}
                  {m.createdBy.name} 记于 {formatTime(m.createdAt)}
                  {m.recallCount > 0 ? ` · 被回忆 ${m.recallCount} 次` : null}
                  {m.tags.length ? ` · ${m.tags.join('、')}` : null}
                </p>
              </div>
              {canDelete(m) ? (
                <Button
                  size='sm'
                  variant='ghost'
                  disabled={remove.isPending}
                  aria-label='忘记这条记忆'
                  onClick={() => void confirm({ title: '忘记这条记忆？', confirmLabel: '忘记' }).then((ok) => ok && remove.mutate(m.id))}
                >
                  <Icons.trash />
                </Button>
              ) : null}
            </li>
          ))}
          {memories.hasNextPage ? (
            <li className='p-2 text-center'>
              <Button size='sm' variant='ghost' disabled={memories.isFetchingNextPage} onClick={() => void memories.fetchNextPage()}>
                {memories.isFetchingNextPage ? '加载中…' : `加载更多（还有 ${(total ?? 0) - items.length} 条）`}
              </Button>
            </li>
          ) : null}
        </ul>
      ) : (
        <EmptyState
          title='还没有记忆'
          description='智能体工作时会自动记下学到的东西；你也可以在这里手动记下，之后的对话和智能体会自动回忆。'
        />
      )}
    </div>
  );
}
