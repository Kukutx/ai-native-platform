'use client';

import { Suspense } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { ConversationView } from '@/features/chat/conversation-view';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

function ChatWorkbench() {
  const router = useRouter();
  const params = useSearchParams();
  const queryClient = useQueryClient();
  const selected = params.get('c');
  const conversations = useQuery({
    queryKey: queryKeys.conversations,
    queryFn: ({ signal }) => api.conversations.list(signal),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.conversations.delete(id),
    onSuccess: async (_, id) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.conversations });
      if (id === selected) {
        router.push('/chat');
      }
    },
  });

  return (
    <div className='flex min-h-0 flex-1'>
      <aside className='hidden w-64 shrink-0 flex-col border-r md:flex'>
        <div className='p-3'>
          <Button className='w-full justify-start' variant='outline' onClick={() => router.push('/chat')}>
            <Icons.add />
            新对话
          </Button>
        </div>
        <nav className='flex-1 overflow-y-auto px-2 pb-3'>
          {conversations.data?.map((conversation) => (
            <div
              key={conversation.id}
              className={cn(
                'group hover:bg-accent flex items-center rounded-md text-sm',
                conversation.id === selected && 'bg-accent font-medium',
              )}
            >
              <button className='flex-1 truncate px-3 py-2 text-left' onClick={() => router.push(`/chat?c=${conversation.id}`)}>
                {conversation.title}
              </button>
              <button
                className='text-muted-foreground hover:text-foreground mr-2 hidden group-hover:block'
                onClick={() => remove.mutate(conversation.id)}
                aria-label='删除对话'
              >
                <Icons.trash className='size-4' />
              </button>
            </div>
          ))}
        </nav>
      </aside>
      <section className='flex min-h-0 min-w-0 flex-1 flex-col'>
        <ConversationView
          key={selected ?? 'new'}
          conversationId={selected}
          onCreated={(id) => router.replace(`/chat?c=${id}`)}
        />
      </section>
    </div>
  );
}

export default function ChatPage() {
  return (
    <Suspense>
      <ChatWorkbench />
    </Suspense>
  );
}
