'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useConfirm } from '@/components/app/confirm-provider';
import { InlineError } from '@/components/app/query-state';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from '@/components/ui/dropdown-menu';
import { Skeleton } from '@/components/ui/skeleton';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

/**
 * 对话列表：桌面端放在左栏，手机上放在抽屉里。onOpen(null) 表示新对话；删除当前对话后回到新对话。
 */
export function ConversationList({ selected, onOpen }: { selected: string | null; onOpen: (id: string | null) => void }) {
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const conversations = useQuery({ queryKey: queryKeys.conversations, queryFn: ({ signal }) => api.conversations.list(signal) });
  const remove = useMutation({
    mutationFn: (id: string) => api.conversations.delete(id),
    meta: { successMessage: '对话已删除' },
    onSuccess: async (_, id) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.conversations });
      if (id === selected) {
        onOpen(null);
      }
    },
  });

  return (
    <div className='flex min-h-0 flex-1 flex-col'>
      <div className='p-3'>
        <Button className='w-full justify-start' variant='outline' onClick={() => onOpen(null)}>
          <Icons.add />
          新对话
        </Button>
      </div>
      <nav className='flex-1 overflow-y-auto px-2 pb-3' aria-label='历史对话'>
        {conversations.isPending ? (
          <div className='space-y-2 px-1'>
            {Array.from({ length: 5 }, (_, i) => (
              <Skeleton key={i} className='h-9' />
            ))}
          </div>
        ) : null}
        {conversations.isError && !conversations.data ? <InlineError query={conversations} className='mx-1' /> : null}
        {conversations.data?.length === 0 ? <p className='text-muted-foreground px-3 py-6 text-center text-sm'>还没有对话，发一条消息就会出现在这里。</p> : null}
        {conversations.data?.map((conversation) => (
          <div
            key={conversation.id}
            className={cn('group hover:bg-accent flex items-center rounded-md text-sm', conversation.id === selected && 'bg-accent font-medium')}
          >
            <button
              type='button'
              className='min-w-0 flex-1 px-3 py-2 text-left'
              aria-current={conversation.id === selected ? 'page' : undefined}
              onClick={() => onOpen(conversation.id)}
            >
              <span className='block truncate'>{conversation.title}</span>
              <span className='text-muted-foreground block text-xs font-normal'>{formatTime(conversation.lastMessageAt)}</span>
            </button>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <Button
                  size='icon'
                  variant='ghost'
                  className='mr-1 size-7 opacity-100 group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100 md:opacity-0'
                  aria-label={`对话「${conversation.title}」的更多操作`}
                >
                  <Icons.dots />
                </Button>
              </DropdownMenuTrigger>
              <DropdownMenuContent align='end'>
                <DropdownMenuItem
                  variant='destructive'
                  disabled={remove.isPending}
                  onSelect={() =>
                    void confirm({ title: `删除对话「${conversation.title}」？`, description: '对话里的消息会一并删除，不能恢复。', confirmLabel: '删除' }).then(
                      (ok) => ok && remove.mutate(conversation.id),
                    )
                  }
                >
                  <Icons.trash />
                  删除
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </div>
        ))}
      </nav>
    </div>
  );
}
