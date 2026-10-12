'use client';

import { useEffect, useRef, useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { ChatMessageDto } from '@ainative/api-client';
import { Markdown } from '@/components/app/markdown';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { api, streamChat } from '@/lib/api';
import { catalogQueries } from '@/lib/catalogs';
import { errorMessage } from '@/lib/errors';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';
import { ToolCallChip } from './tool-call-chip';

interface Draft {
  user: string;
  text: string;
  tools: { callId: string; actionKey: string; status?: string }[];
}

function lastUserMessageIs(messages: ChatMessageDto[] | undefined, text: string) {
  return [...(messages ?? [])].reverse().find((m) => m.role === 'user')?.text === text;
}

/**
 * 对话视图。conversationId 为空时是"新对话"：发送第一条消息时才创建会话，完成后通过 onCreated 切换地址。
 */
export function ConversationView({ conversationId, onCreated }: { conversationId: string | null; onCreated?: (id: string) => void }) {
  const queryClient = useQueryClient();
  const { can } = useSession();
  const [activeId, setActiveId] = useState(conversationId);
  const models = useQuery({ ...catalogQueries.models(), enabled: can('agents.view') });
  const demo = models.data?.providers.find((p) => p.name === models.data.defaultProvider)?.kind === 'demo';
  const messages = useQuery({
    queryKey: queryKeys.messages(activeId ?? 'new'),
    queryFn: ({ signal }) => api.conversations.messages(activeId!, signal),
    enabled: !!activeId,
  });
  const [input, setInput] = useState('');
  const [draft, setDraft] = useState<Draft | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages.data, draft]);

  useEffect(() => () => abortRef.current?.abort(), []);

  async function send() {
    const text = input.trim();
    if (!text || draft) {
      return;
    }

    setInput('');
    setDraft({ user: text, text: '', tools: [] });
    const controller = new AbortController();
    abortRef.current = controller;
    let id = activeId;
    let failed = false;
    try {
      if (!id) {
        id = (await api.conversations.create({ title: text.slice(0, 40) }, controller.signal)).id;
        setActiveId(id);
      }

      for await (const event of streamChat(id, text, controller.signal)) {
        if (event.type === 'text_delta') {
          setDraft((d) => d && { ...d, text: d.text + (event.text ?? '') });
        } else if (event.type === 'tool_call') {
          setDraft((d) => d && { ...d, tools: [...d.tools, { callId: event.callId!, actionKey: event.actionKey! }] });
        } else if (event.type === 'tool_result') {
          const status = (event.data as { status?: string } | undefined)?.status;
          setDraft((d) => d && { ...d, tools: d.tools.map((t) => (t.callId === event.callId ? { ...t, status } : t)) });
        } else if (event.type === 'error') {
          failed = true;
          toast.error(event.text ?? '对话出错了');
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        failed = true;
        toast.error(`对话中断：${errorMessage(error)}`);
      }
    } finally {
      if (id) {
        await queryClient.invalidateQueries({ queryKey: queryKeys.messages(id) });
        await queryClient.invalidateQueries({ queryKey: queryKeys.conversations });
      }

      setDraft(null);
      // 消息没能保存（例如在别处发送的上一条还在回复中）时放回输入框，用户不用重新输入。
      if (failed && !lastUserMessageIs(id ? queryClient.getQueryData<ChatMessageDto[]>(queryKeys.messages(id)) : undefined, text)) {
        setInput((current) => current || text);
      }
      if (id && !conversationId) {
        onCreated?.(id);
      }
    }
  }

  return (
    <div className='flex min-h-0 flex-1 flex-col'>
      <div className='flex-1 overflow-y-auto'>
        <div className='mx-auto flex max-w-3xl flex-col gap-6 px-4 py-6'>
          {messages.isLoading ? <Skeleton className='h-20 w-full' /> : null}
          {!messages.data?.length && !messages.isLoading && !draft ? (
            <div className='text-muted-foreground py-16 text-center text-sm'>
              <p className='text-foreground mb-2 text-base font-medium'>有什么可以帮你？</p>
              {demo ? (
                <>当前使用内置演示模型，它只会复述你的话，不会真的执行操作。在后端配置 Ai:Providers 的模型密钥后，就可以让我建任务、规划项目、查客户。</>
              ) : (
                <>试试：“帮我建一个任务：整理本周客户反馈，交给小智”，或“现在团队里都有谁？”</>
              )}
            </div>
          ) : null}
          {messages.data?.map((message) => <MessageBubble key={message.id} message={message} />)}
          {draft ? (
            <>
              <Bubble role='user'>{draft.user}</Bubble>
              <Bubble role='assistant'>
                {draft.tools.length ? (
                  <div className='mb-2 flex flex-wrap gap-1'>
                    {draft.tools.map((t) => (
                      <ToolCallChip key={t.callId} actionKey={t.actionKey} status={t.status} />
                    ))}
                  </div>
                ) : null}
                {draft.text ? <Markdown>{draft.text}</Markdown> : <Icons.dots className='text-muted-foreground size-4 animate-pulse' aria-label='正在回复' />}
              </Bubble>
            </>
          ) : null}
          <div ref={bottomRef} />
        </div>
      </div>
      <div className='border-t p-4'>
        <form
          className='mx-auto flex max-w-3xl items-end gap-2'
          onSubmit={(event) => {
            event.preventDefault();
            void send();
          }}
        >
          <Textarea
            value={input}
            onChange={(event) => setInput(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter' && !event.shiftKey && !event.nativeEvent.isComposing) {
                event.preventDefault();
                void send();
              }
            }}
            placeholder='输入消息，Enter 发送，Shift+Enter 换行'
            className='max-h-48 min-h-11 resize-none'
            rows={1}
          />
          {draft ? (
            <Button type='button' variant='outline' size='icon' onClick={() => abortRef.current?.abort()} aria-label='停止'>
              <Icons.close />
            </Button>
          ) : (
            <Button type='submit' size='icon' disabled={!input.trim()} aria-label='发送'>
              <Icons.send />
            </Button>
          )}
        </form>
      </div>
    </div>
  );
}

function MessageBubble({ message }: { message: ChatMessageDto }) {
  const role = message.role === 'user' ? 'user' : 'assistant';
  return (
    <Bubble role={role}>
      {message.toolCalls.length ? (
        <div className='mb-2 flex flex-wrap gap-1'>
          {message.toolCalls.map((call) => (
            <ToolCallChip key={call.callId} actionKey={call.actionKey} status={call.status} />
          ))}
        </div>
      ) : null}
      {/* 模型的回复按 Markdown 显示；用户自己输入的内容照原样显示。 */}
      {role === 'user' ? message.text : <Markdown>{message.text}</Markdown>}
    </Bubble>
  );
}

function Bubble({ role, children }: { role: 'user' | 'assistant'; children: React.ReactNode }) {
  return (
    <div className={cn('flex', role === 'user' ? 'justify-end' : 'justify-start')}>
      <div
        className={cn(
          'max-w-[85%] min-w-0 rounded-2xl px-4 py-2.5 text-sm leading-relaxed',
          role === 'user' ? 'bg-primary text-primary-foreground whitespace-pre-wrap' : 'bg-muted',
        )}
      >
        {children}
      </div>
    </div>
  );
}
