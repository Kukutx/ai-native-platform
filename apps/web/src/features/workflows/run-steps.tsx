import Link from 'next/link';
import type { WorkflowRunDto, WorkflowRunStepDto, WorkflowStep } from '@ainative/api-client';
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

/** 按定义列出每一步，并附上本次运行的结果（运行使用启动时的定义快照）。分支只展开走到的那一支，遍历按项展开。 */
export function RunSteps({ run }: { run: WorkflowRunDto }) {
  const byPath = new Map(run.stepRuns.map((s) => [s.path, s]));
  const finished = ['succeeded', 'failed', 'cancelled'].includes(run.summary.status);
  return <StepRunList run={run} steps={run.steps} pathOf={(i) => `${i}`} byPath={byPath} finished={finished} />;
}

function StepRunList({
  run,
  steps,
  pathOf,
  byPath,
  finished,
}: {
  run: WorkflowRunDto;
  steps: WorkflowStep[];
  pathOf: (index: number) => string;
  byPath: Map<string, WorkflowRunStepDto>;
  finished: boolean;
}) {
  return (
    <ol className='relative space-y-5 border-l pl-6'>
      {steps.map((step, index) => {
        const path = pathOf(index);
        const stepRun = byPath.get(path);
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
                {step.kind === 'llm' && typeof stepRun.output?.text === 'string' ? <p className='text-sm whitespace-pre-wrap'>{stepRun.output.text}</p> : null}
                {step.kind === 'agent_task' && typeof stepRun.output?.result === 'string' ? <p className='text-sm whitespace-pre-wrap'>{stepRun.output.result}</p> : null}
                {step.kind === 'http' && typeof stepRun.output?.status === 'number' ? <p className='text-sm'>HTTP {stepRun.output.status}</p> : null}
                {step.kind !== 'for_each' ? <Json label='输入' value={stepRun.input} /> : null}
                {step.kind !== 'for_each' && step.kind !== 'branch' ? <Json label='输出' value={stepRun.output} /> : null}
                {step.kind === 'branch' ? <BranchRun run={run} step={step} stepRun={stepRun} path={path} byPath={byPath} finished={finished} /> : null}
                {step.kind === 'for_each' ? <LoopRun run={run} step={step} stepRun={stepRun} path={path} byPath={byPath} finished={finished} /> : null}
              </div>
            ) : null}
          </li>
        );
      })}
    </ol>
  );
}

interface NestedProps {
  run: WorkflowRunDto;
  step: WorkflowStep;
  stepRun: WorkflowRunStepDto;
  path: string;
  byPath: Map<string, WorkflowRunStepDto>;
  finished: boolean;
}

function BranchRun({ run, step, stepRun, path, byPath, finished }: NestedProps) {
  const chosen = typeof stepRun.input?.branch === 'number' ? (stepRun.input.branch as number) : (stepRun.output?.index as number | undefined);
  const branch = chosen !== undefined && chosen >= 0 ? step.branches?.[chosen] : undefined;
  if (!branch) {
    return stepRun.status === 'succeeded' ? <p className='text-muted-foreground text-sm'>没有成立的分支，跳过。</p> : null;
  }

  return (
    <div className='mt-1 grid gap-2'>
      <p className='text-sm'>
        走了「{branch.name}」
        {branch.when ? <span className='text-muted-foreground ml-2 font-mono text-xs'>{branch.when}</span> : null}
      </p>
      {branch.steps?.length ? <StepRunList run={run} steps={branch.steps} pathOf={(i) => `${path}.${chosen}.${i}`} byPath={byPath} finished={finished} /> : null}
    </div>
  );
}

function LoopRun({ run, step, stepRun, path, byPath, finished }: NestedProps) {
  const items = Array.isArray(stepRun.input?.items) ? (stepRun.input.items as unknown[]) : [];
  if (!items.length) {
    return <p className='text-muted-foreground text-sm'>{stepRun.status === 'succeeded' ? '列表为空，没有执行。' : null}</p>;
  }

  // 只展开进行中、失败的项，和总数不多时的全部项。
  const started = (i: number) => byPath.has(`${path}.${i}.0`);
  const attention = (i: number) =>
    (step.steps ?? []).some((_, j) => ['running', 'waiting', 'failed'].includes(byPath.get(`${path}.${i}.${j}`)?.status ?? ''));
  return (
    <div className='mt-1 grid gap-2'>
      <p className='text-muted-foreground text-sm'>共 {items.length} 项</p>
      {items.map((item, i) =>
        started(i) || !finished ? (
          <Collapsible key={i} defaultOpen={items.length <= 3 || attention(i)}>
            <CollapsibleTrigger className='hover:text-foreground flex max-w-full items-center gap-1 text-sm'>
              <Icons.chevronRight className='size-3 shrink-0' />
              <span className='shrink-0'>第 {i + 1} 项</span>
              <span className='text-muted-foreground truncate font-mono text-xs'>{preview(item)}</span>
            </CollapsibleTrigger>
            <CollapsibleContent className='pt-2'>
              <StepRunList run={run} steps={step.steps ?? []} pathOf={(j) => `${path}.${i}.${j}`} byPath={byPath} finished={finished} />
            </CollapsibleContent>
          </Collapsible>
        ) : null,
      )}
    </div>
  );
}

function preview(value: unknown) {
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  return text.length > 80 ? `${text.slice(0, 80)}…` : text;
}
