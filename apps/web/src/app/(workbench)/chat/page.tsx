'use client';

import { Suspense, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet';
import { ConversationList } from '@/features/chat/conversation-list';
import { ConversationView } from '@/features/chat/conversation-view';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

function ChatWorkbench() {
  const router = useRouter();
  const params = useSearchParams();
  const selected = params.get('c');
  const [drawerOpen, setDrawerOpen] = useState(false);
  // 与对话列表共用同一个查询，这里只用来在手机顶栏显示当前对话的标题。
  const conversations = useQuery({ queryKey: queryKeys.conversations, queryFn: ({ signal }) => api.conversations.list(signal) });
  const current = conversations.data?.find((c) => c.id === selected);
  const open = (id: string | null) => {
    setDrawerOpen(false);
    router.push(id ? `/chat?c=${id}` : '/chat');
  };

  return (
    <div className='flex min-h-0 flex-1'>
      <aside className='hidden w-64 shrink-0 flex-col border-r md:flex'>
        <ConversationList selected={selected} onOpen={open} />
      </aside>
      <section className='flex min-h-0 min-w-0 flex-1 flex-col'>
        {/* 手机上左栏放不下：顶栏显示当前对话，历史对话放进抽屉。 */}
        <div className='flex items-center gap-2 border-b px-3 py-2 md:hidden'>
          <Sheet open={drawerOpen} onOpenChange={setDrawerOpen}>
            <SheetTrigger asChild>
              <Button variant='ghost' size='sm' className='min-w-0 flex-1 justify-start'>
                <Icons.chat />
                <span className='truncate'>{selected ? (current?.title ?? '对话') : '新对话'}</span>
                <Icons.chevronDown className='text-muted-foreground ml-auto' />
              </Button>
            </SheetTrigger>
            <SheetContent side='left' className='w-72 gap-0 p-0'>
              <SheetHeader className='border-b px-4 pb-3'>
                <SheetTitle>历史对话</SheetTitle>
                <SheetDescription className='sr-only'>选择一个对话继续，或开始新对话。</SheetDescription>
              </SheetHeader>
              <ConversationList selected={selected} onOpen={open} />
            </SheetContent>
          </Sheet>
          {selected ? (
            <Button variant='outline' size='icon' className='size-8' onClick={() => open(null)} aria-label='新对话'>
              <Icons.add />
            </Button>
          ) : null}
        </div>
        <ConversationView key={selected ?? 'new'} conversationId={selected} onCreated={(id) => router.replace(`/chat?c=${id}`)} />
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
