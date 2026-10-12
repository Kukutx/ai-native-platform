'use client';

import { toast } from 'sonner';
import type { ActionDescriptorDto, AgentDto, WorkflowEventDto, WorkflowStep, WorkflowStepKind, WorkflowTriggerDto } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader } from '@/components/ui/card';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { workflowStepKind } from '@/lib/labels';
import { inputSkeleton, referencesFor, schemaProperties } from './describe';

export interface DraftStep {
  /** 编辑期间的稳定标识（步骤 key 可以被改）。 */
  uid: string;
  step: WorkflowStep;
  /** 动作输入按 JSON 文本编辑，保存时再解析。 */
  inputText: string;
}

export type FieldErrors = Record<string, string[] | undefined>;

export function FieldError({ errors, path }: { errors: FieldErrors; path: string }) {
  const message = errors[path]?.[0];
  return message ? <p className='text-destructive text-xs'>{message}</p> : null;
}

const kinds = Object.keys(workflowStepKind) as WorkflowStepKind[];

export function StepCard({
  index,
  draft,
  steps,
  trigger,
  events,
  actions,
  agents,
  errors,
  onChange,
  onMove,
  onRemove,
}: {
  index: number;
  draft: DraftStep;
  steps: WorkflowStep[];
  trigger: WorkflowTriggerDto;
  events?: WorkflowEventDto[];
  actions?: ActionDescriptorDto[];
  agents?: AgentDto[];
  errors: FieldErrors;
  onChange: (draft: DraftStep) => void;
  onMove?: (delta: -1 | 1) => void;
  onRemove: () => void;
}) {
  const { step } = draft;
  const kind = step.kind ?? 'action';
  const path = `steps[${index}]`;
  const id = (field: string) => `${draft.uid}-${field}`;
  const set = (patch: Partial<WorkflowStep>) => onChange({ ...draft, step: { ...step, ...patch } });
  const action = actions?.find((a) => a.key === step.actionKey);
  const references = referencesFor(index, steps, trigger, events, actions);

  return (
    <Card>
      <CardHeader className='flex flex-row items-center gap-3 space-y-0'>
        <Badge variant='outline' className='font-mono'>
          {index + 1}
        </Badge>
        <Input
          aria-label='步骤名称'
          className='max-w-xs'
          value={step.name}
          maxLength={100}
          placeholder='这一步做什么'
          onChange={(e) => set({ name: e.target.value })}
        />
        <Select value={kind} onValueChange={(value) => set({ kind: value as WorkflowStepKind })}>
          <SelectTrigger className='w-36' aria-label='步骤类型'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((k) => (
              <SelectItem key={k} value={k}>
                {workflowStepKind[k].label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className='ml-auto flex items-center gap-1'>
          <Button type='button' size='icon' variant='ghost' aria-label='上移' disabled={!onMove || index === 0} onClick={() => onMove?.(-1)}>
            <Icons.arrowUp className='size-4' />
          </Button>
          <Button type='button' size='icon' variant='ghost' aria-label='下移' disabled={!onMove || index === steps.length - 1} onClick={() => onMove?.(1)}>
            <Icons.arrowDown className='size-4' />
          </Button>
          <Button type='button' size='icon' variant='ghost' aria-label='删除步骤' onClick={onRemove}>
            <Icons.trash className='size-4' />
          </Button>
        </div>
      </CardHeader>
      <CardContent className='grid gap-4'>
        <FieldError errors={errors} path={`${path}.name`} />
        <p className='text-muted-foreground text-xs'>{workflowStepKind[kind].hint}</p>

        {kind === 'action' ? (
          <>
            <div className='grid gap-2'>
              <Label>动作</Label>
              <Select
                value={step.actionKey ?? ''}
                onValueChange={(value) => {
                  const next = actions?.find((a) => a.key === value);
                  const blank = !draft.inputText.trim() || draft.inputText.replace(/\s/g, '') === '{}';
                  onChange({
                    ...draft,
                    step: { ...step, actionKey: value, name: step.name || next?.title || '' },
                    inputText: blank && next ? inputSkeleton(next.inputSchema) : draft.inputText,
                  });
                }}
              >
                <SelectTrigger className='w-full'>
                  <SelectValue placeholder='选择要执行的动作' />
                </SelectTrigger>
                <SelectContent>
                  {actions?.map((a) => (
                    <SelectItem key={a.key} value={a.key}>
                      {a.title} <span className='text-muted-foreground font-mono text-xs'>{a.key}</span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              {action ? <p className='text-muted-foreground text-xs'>{action.description}</p> : null}
              <FieldError errors={errors} path={`${path}.actionKey`} />
            </div>
            <div className='grid gap-2'>
              <Label htmlFor={id('input')}>输入（JSON）</Label>
              <Textarea
                id={id('input')}
                className='font-mono text-sm'
                rows={Math.min(14, Math.max(4, draft.inputText.split('\n').length + 1))}
                value={draft.inputText}
                onChange={(e) => onChange({ ...draft, inputText: e.target.value })}
                aria-invalid={!!errors[`${path}.input`]}
              />
              <FieldError errors={errors} path={`${path}.input`} />
              {action ? (
                <ul className='text-muted-foreground grid gap-1 text-xs'>
                  {schemaProperties(action.inputSchema).map((p) => (
                    <li key={p.name}>
                      <code className='text-foreground'>{p.name}</code>
                      <span className='font-mono'> · {p.type}</span>
                      {p.required ? <span className='text-destructive'> · 必填</span> : null}
                      {p.description ? ` · ${p.description}` : null}
                    </li>
                  ))}
                </ul>
              ) : null}
            </div>
          </>
        ) : null}

        {kind === 'agent_task' ? (
          <>
            <div className='grid gap-4 sm:grid-cols-2'>
              <div className='grid gap-2'>
                <Label>智能体</Label>
                <Select value={step.agentId ?? ''} onValueChange={(value) => set({ agentId: value })}>
                  <SelectTrigger className='w-full'>
                    <SelectValue placeholder='交给谁' />
                  </SelectTrigger>
                  <SelectContent>
                    {agents
                      ?.filter((a) => a.status !== 'terminated')
                      .map((a) => (
                        <SelectItem key={a.id} value={a.id}>
                          {a.name} · {a.title}
                        </SelectItem>
                      ))}
                  </SelectContent>
                </Select>
                <FieldError errors={errors} path={`${path}.agentId`} />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor={id('title')}>任务标题</Label>
                <Input id={id('title')} value={step.title ?? ''} maxLength={300} onChange={(e) => set({ title: e.target.value })} placeholder='例如：调研 {{ input.topic }}' />
                <FieldError errors={errors} path={`${path}.title`} />
              </div>
            </div>
            <div className='grid gap-2'>
              <Label htmlFor={id('instructions')}>任务说明</Label>
              <Textarea id={id('instructions')} rows={4} value={step.instructions ?? ''} onChange={(e) => set({ instructions: e.target.value })} />
              <FieldError errors={errors} path={`${path}.instructions`} />
            </div>
            <div className='flex flex-wrap items-center gap-4'>
              <label className='flex items-center gap-2 text-sm'>
                <input
                  type='checkbox'
                  className='accent-primary size-4'
                  checked={step.waitForCompletion ?? false}
                  onChange={(e) => set({ waitForCompletion: e.target.checked })}
                />
                等它完成后再继续
              </label>
              {step.waitForCompletion ? (
                <div className='flex items-center gap-2 text-sm'>
                  <Label htmlFor={id('timeout')}>最多等</Label>
                  <Input
                    id={id('timeout')}
                    type='number'
                    min={1}
                    max={43200}
                    className='w-24'
                    value={step.timeoutMinutes ?? ''}
                    placeholder='不限'
                    onChange={(e) => set({ timeoutMinutes: e.target.value ? Number(e.target.value) : undefined })}
                  />
                  分钟
                </div>
              ) : null}
            </div>
            <FieldError errors={errors} path={`${path}.timeoutMinutes`} />
          </>
        ) : null}

        {kind === 'llm' ? (
          <div className='grid gap-2'>
            <Label htmlFor={id('prompt')}>提示词</Label>
            <Textarea
              id={id('prompt')}
              rows={5}
              value={step.prompt ?? ''}
              onChange={(e) => set({ prompt: e.target.value })}
              placeholder='例如：把下面的客户反馈分类为 bug / 需求 / 咨询，只输出 JSON {"type": "..."}：{{ trigger.task.description }}'
            />
            <FieldError errors={errors} path={`${path}.prompt`} />
          </div>
        ) : null}

        {kind === 'delay' ? (
          <div className='grid gap-2'>
            <Label htmlFor={id('delay')}>等待时长（秒）</Label>
            <Input
              id={id('delay')}
              type='number'
              min={1}
              className='w-40'
              value={step.delaySeconds ?? ''}
              onChange={(e) => set({ delaySeconds: e.target.value ? Number(e.target.value) : undefined })}
            />
            {step.delaySeconds && step.delaySeconds >= 60 ? (
              <p className='text-muted-foreground text-xs'>约 {formatDuration(step.delaySeconds)}</p>
            ) : null}
            <FieldError errors={errors} path={`${path}.delaySeconds`} />
          </div>
        ) : null}

        <Collapsible>
          <CollapsibleTrigger asChild>
            <Button type='button' variant='ghost' size='sm' className='text-muted-foreground -ml-2'>
              <Icons.chevronRight className='size-4' />
              条件、标识与可用变量
            </Button>
          </CollapsibleTrigger>
          <CollapsibleContent className='grid gap-4 pt-2'>
            <div className='grid gap-4 sm:grid-cols-2'>
              <div className='grid gap-2'>
                <Label htmlFor={id('key')}>标识</Label>
                <Input
                  id={id('key')}
                  className='font-mono'
                  value={step.key}
                  maxLength={40}
                  onChange={(e) => set({ key: e.target.value })}
                  aria-invalid={!!errors[`${path}.key`]}
                />
                <p className='text-muted-foreground text-xs'>后面的步骤用 steps.{step.key || '标识'}.output 引用本步结果。</p>
                <FieldError errors={errors} path={`${path}.key`} />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor={id('when')}>执行条件</Label>
                <Input
                  id={id('when')}
                  className='font-mono'
                  value={step.when ?? ''}
                  placeholder="留空表示总是执行，例如 input.amount > 1000"
                  onChange={(e) => set({ when: e.target.value || undefined })}
                />
                <FieldError errors={errors} path={`${path}.when`} />
              </div>
            </div>
            <label className='flex items-center gap-2 text-sm'>
              <input
                type='checkbox'
                className='accent-primary size-4'
                checked={step.continueOnError ?? false}
                onChange={(e) => set({ continueOnError: e.target.checked })}
              />
              这一步失败时继续执行后面的步骤
            </label>
            <div className='grid gap-1'>
              <p className='text-sm font-medium'>可用变量</p>
              <p className='text-muted-foreground text-xs'>文本里写 {'{{ 变量 }}'}，执行条件里直接写变量。点击复制。</p>
              <ul className='grid gap-1 text-xs'>
                {references.map((ref) => (
                  <li key={ref.expr} className='flex flex-wrap items-baseline gap-2'>
                    <button
                      type='button'
                      className='bg-muted hover:bg-accent rounded px-1.5 py-0.5 font-mono'
                      onClick={() => {
                        void navigator.clipboard.writeText(`{{ ${ref.expr} }}`).then(() => toast.success('已复制'));
                      }}
                    >
                      {ref.expr}
                    </button>
                    <span className='text-muted-foreground'>{ref.hint}</span>
                  </li>
                ))}
              </ul>
            </div>
          </CollapsibleContent>
        </Collapsible>
      </CardContent>
    </Card>
  );
}

function formatDuration(seconds: number) {
  if (seconds >= 86400) {
    return `${(seconds / 86400).toFixed(1).replace(/\.0$/, '')} 天`;
  }

  if (seconds >= 3600) {
    return `${(seconds / 3600).toFixed(1).replace(/\.0$/, '')} 小时`;
  }

  return `${Math.round(seconds / 60)} 分钟`;
}
