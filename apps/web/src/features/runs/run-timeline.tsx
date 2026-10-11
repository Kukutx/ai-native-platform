import Link from 'next/link';
import type { RunEventDto } from '@ainative/api-client';
import { ToolCallChip } from '@/features/chat/tool-call-chip';
import { formatTime, runStatus } from '@/lib/labels';

type EventData = Record<string, unknown>;

function Json({ value }: { value: unknown }) {
  if (value === undefined || value === null) {
    return null;
  }

  return <pre className='bg-muted mt-2 max-h-64 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(value, null, 2)}</pre>;
}

function EventBody({ event }: { event: RunEventDto }) {
  const data = (event.data ?? {}) as EventData;
  switch (event.type) {
    case 'status': {
      const status = data.status as keyof typeof runStatus | undefined;
      const reasons = Array.isArray(data.reasons) ? (data.reasons as string[]) : [];
      return (
        <div className='text-sm'>
          <span className='font-medium'>{status ? (runStatus[status]?.label ?? status) : '状态变化'}</span>
          {reasons.length ? <ul className='text-muted-foreground mt-1 list-disc pl-5'>{reasons.map((r, i) => <li key={i}>{r}</li>)}</ul> : null}
          {typeof data.summary === 'string' ? <p className='mt-1 whitespace-pre-wrap'>{data.summary}</p> : null}
        </div>
      );
    }
    case 'assistant_text':
      return <p className='text-sm whitespace-pre-wrap'>{String(data.text ?? '')}</p>;
    case 'tool_call':
      return (
        <div>
          <ToolCallChip actionKey={String(data.actionKey ?? '')} />
          <Json value={data.arguments} />
        </div>
      );
    case 'tool_result':
    case 'approval_requested':
      return (
        <div>
          <ToolCallChip actionKey={String(data.actionKey ?? '')} status={typeof data.status === 'string' ? data.status : undefined} />
          {typeof data.error === 'string' ? <p className='text-destructive mt-1 text-sm'>{data.error}</p> : null}
          {event.type === 'approval_requested' ? (
            <p className='mt-1 text-sm'>
              已提交人工审批，
              <Link href='/approvals' className='underline'>
                去处理
              </Link>
            </p>
          ) : (
            <Json value={data.output} />
          )}
        </div>
      );
    case 'error':
      return <p className='text-destructive text-sm whitespace-pre-wrap'>{String(data.error ?? '执行失败')}</p>;
    default:
      return <Json value={data} />;
  }
}

const typeLabel: Record<RunEventDto['type'], string> = {
  status: '状态',
  assistant_text: '回复',
  tool_call: '调用动作',
  tool_result: '动作结果',
  approval_requested: '等待审批',
  error: '错误',
};

/** 运行过程时间线：持久化事件按序号展示，末尾附加尚未落库的逐字输出。 */
export function RunTimeline({ events, liveText }: { events: RunEventDto[]; liveText?: string }) {
  return (
    <ol className='relative space-y-5 border-l pl-6'>
      {events.map((event) => (
        <li key={event.seq} className='relative'>
          <span className='bg-background absolute top-1.5 -left-[29px] size-2.5 rounded-full border-2' />
          <div className='text-muted-foreground mb-1 flex items-center gap-2 text-xs'>
            <span className='font-medium'>{typeLabel[event.type]}</span>
            <span>#{event.seq}</span>
            <span>{formatTime(event.createdAt)}</span>
          </div>
          <EventBody event={event} />
        </li>
      ))}
      {liveText ? (
        <li className='relative'>
          <span className='bg-primary absolute top-1.5 -left-[29px] size-2.5 animate-pulse rounded-full' />
          <div className='text-muted-foreground mb-1 text-xs font-medium'>正在输出…</div>
          <p className='text-sm whitespace-pre-wrap'>{liveText}</p>
        </li>
      ) : null}
    </ol>
  );
}
