'use client';

import { Suspense, useState } from 'react';
import { useRouter, useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle, SheetTrigger } from '@/components/ui/sheet';
import { ChatAgentSelect, useChatAgents } from '@/features/chat/chat-agents';
import { ConversationList } from '@/features/chat/conversation-list';
import { ConversationView } from '@/features/chat/conversation-view';
import { useDocumentTitle } from '@/hooks/use-document-title';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

function ChatWorkbench() {
  const router = useRouter();
  const params = useSearchParams();
  const selected = params.get('c');
  // 新对话的对话对象记在地址里（?agent=），智能体详情页可以直接链接过来。
  const requestedAgent = params.get('agent') ?? undefined;
  const [drawerOpen, setDrawerOpen] = useState(false);
  const agents = useChatAgents();
  // 与对话列表共用同一个查询，这里用来在标题区显示当前对话的标题和对话对象。
  const conversations = useQuery({ queryKey: queryKeys.conversations, queryFn: ({ signal }) => api.conversations.list(signal) });
  const current = conversations.data?.find((c) => c.id === selected);
  const newAgent = agents.available.find((a) => a.id === requestedAgent);
  const open = (id: string | null) => {
    setDrawerOpen(false);
    router.push(id ? `/chat?c=${id}` : '/chat');
  };
  const who = selected ? (current ? agents.label(current.agentId) : '') : (newAgent?.name ?? agents.label(undefined));
  const title = selected ? (current?.title ?? '对话') : '新对话';
  useDocumentTitle(title);

  return (
    <div className='flex min-h-0 flex-1'>
      <aside className='hidden w-64 shrink-0 flex-col border-r md:flex'>
        <ConversationList selected={selected} onOpen={open} />
      </aside>
      <section className='flex min-h-0 min-w-0 flex-1 flex-col' aria-label='对话'>
        <header className='flex items-center gap-2 border-b px-3 py-2 md:px-4'>
          {/* 手机上左栏放不下：历史对话放进抽屉。 */}
          <Sheet open={drawerOpen} onOpenChange={setDrawerOpen}>
            <SheetTrigger asChild>
              <Button variant='ghost' size='icon' className='size-8 md:hidden' aria-label='历史对话'>
                <Icons.chat />
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
          <div className='min-w-0 flex-1'>
            <h1 className='truncate text-sm font-medium'>{title}</h1>
            <p className='text-muted-foreground flex items-center gap-1 truncate text-xs'>
              {(selected ? current?.agentId : newAgent) ? <Icons.agent className='size-3 shrink-0' /> : <Icons.sparkles className='size-3 shrink-0' />}
              {who ? `与${who}对话` : ' '}
            </p>
          </div>
          {!selected && agents.visible ? (
            <ChatAgentSelect
              value={newAgent?.id}
              agents={agents.available}
              onChange={(agentId) => router.replace(agentId ? `/chat?agent=${agentId}` : '/chat', { scroll: false })}
            />
          ) : null}
          {selected ? (
            <Button variant='outline' size='icon' className='size-8 md:hidden' onClick={() => open(null)} aria-label='新对话'>
              <Icons.add />
            </Button>
          ) : null}
        </header>
        <ConversationView
          key={selected ?? 'new'}
          conversationId={selected}
          agent={selected ? undefined : newAgent}
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
