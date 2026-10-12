import Link from 'next/link';
import type { WorkflowRunDto, WorkflowRunStepDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Icons } from '@/components/icons';
import { formatTime, workflowStepKind, workflowStepStatus } from '@/lib/labels';
import { cn } from '@/lib/utils';

function Json({ label, value }: { label: string; value: unknown }) {
  if (value === undefined || value === null) {
    return null;
  }

  return (
    <Collapsible>
      <CollapsibleTrigger className='text-muted-foreground hover:text-foreground flex items-center gap-1 text-xs'>
        <Icons.chevronRight className='size-3' />
        {label}
      </CollapsibleTrigger>
      <CollapsibleContent>
        <pre className='bg-muted mt-1 max-h-72 overflow-auto rounded-md p-3 font-mono text-xs'>{JSON.stringify(value, null, 2)}</pre>
      </CollapsibleContent>
    </Collapsible>
  );
}

function Waiting({ run, stepRun }: { run: WorkflowRunDto; stepRun: WorkflowRunStepDto }) {
  if (stepRun.status !== 'waiting' || run.summary.status !== 'waiting') {
    return null;
  }

  if (run.waitingApprovalId) {
    return (
      <p className='text-sm'>
        等待人工审批，
        <Link href='/approvals' className='underline'>
          去处理
        </Link>
      </p>
    );
  }

  if (run.waitingWorkItemId) {
    return (
      <p className='text-sm'>
        等待智能体完成
        <Link href={`/tasks/view?id=${run.waitingWorkItemId}`} className='ml-1 underline'>
          任务
        </Link>
        {run.resumeAt ? `，最晚 ${formatTime(run.resumeAt)} 超时` : ''}
      </p>
    );
  }

  return run.resumeAt ? <p className='text-sm'>将在 {formatTime(run.resumeAt)} 继续</p> : null;
}

/** 按定义列出每一步，并附上本次运行的结果（运行使用启动时的定义快照）。 */
export function RunSteps({ run }: { run: WorkflowRunDto }) {
  const byIndex = new Map(run.stepRuns.map((s) => [s.index, s]));
  const finished = ['succeeded', 'failed', 'cancelled'].includes(run.summary.status);

  return (
    <ol className='relative space-y-5 border-l pl-6'>
      {run.steps.map((step, index) => {
        const stepRun = byIndex.get(index);
        const active = stepRun?.status === 'running' || stepRun?.status === 'waiting';
        return (
          <li key={step.key} className='relative'>
            <span
              className={cn(
                'bg-background absolute top-1.5 -left-[29px] size-2.5 rounded-full border-2',
                active && 'bg-primary border-primary animate-pulse',
                stepRun?.status === 'succeeded' && 'bg-primary border-primary',
                stepRun?.status === 'failed' && 'bg-destructive border-destructive',
              )}
            />
            <div className='flex flex-wrap items-center gap-2'>
              <span className='font-medium'>{step.name}</span>
              <span className='text-muted-foreground text-xs'>{workflowStepKind[step.kind ?? 'action'].label}</span>
              {stepRun ? (
                <StatusBadge meta={workflowStepStatus[stepRun.status]} />
              ) : (
                <span className='text-muted-foreground text-xs'>{finished ? '未执行' : '待执行'}</span>
              )}
              {stepRun ? (
                <span className='text-muted-foreground text-xs'>
                  {formatTime(stepRun.startedAt)}
                  {stepRun.finishedAt ? ` → ${formatTime(stepRun.finishedAt)}` : ''}
                </span>
              ) : null}
            </div>
            {stepRun ? (
              <div className='mt-1 grid gap-1'>
                <Waiting run={run} stepRun={stepRun} />
                {stepRun.error ? <p className='text-destructive text-sm whitespace-pre-wrap'>{stepRun.error}</p> : null}
                {step.kind === 'llm' && typeof stepRun.output?.text === 'string' ? (
                  <p className='text-sm whitespace-pre-wrap'>{stepRun.output.text}</p>
                ) : null}
                {step.kind === 'agent_task' && typeof stepRun.output?.result === 'string' ? (
                  <p className='text-sm whitespace-pre-wrap'>{stepRun.output.result}</p>
                ) : null}
                <Json label='输入' value={stepRun.input} />
                <Json label='输出' value={stepRun.output} />
              </div>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}
