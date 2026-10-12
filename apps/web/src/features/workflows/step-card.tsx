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
import { workflowControlKinds, workflowStepKind } from '@/lib/labels';
import { cn } from '@/lib/utils';
import { inputSkeleton, referencesFor, schemaProperties } from './describe';
import { flatten, MAX_BRANCHES, MAX_NESTING, newDraftStep, nextUid, withDefaultBranches, type DraftBranch, type DraftStep, type FieldErrors } from './draft';

export function FieldError({ errors, path }: { errors: FieldErrors; path: string }) {
  const message = errors[path]?.[0];
  return message ? <p className='text-destructive text-xs'>{message}</p> : null;
}

/** 编辑器里各层步骤共用的数据。 */
export interface StepEditorContext {
  trigger: WorkflowTriggerDto;
  events?: WorkflowEventDto[];
  actions?: ActionDescriptorDto[];
  agents?: AgentDto[];
  errors: FieldErrors;
  /** 整个工作流里没用过的步骤标识。 */
  newKey: () => string;
}

const allKinds = Object.keys(workflowStepKind) as WorkflowStepKind[];
const httpMethods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE'];

/** 嵌套层数用完后不能再放分支和遍历。 */
const kindsAt = (depth: number) => (depth >= MAX_NESTING ? allKinds.filter((k) => !workflowControlKinds.includes(k)) : allKinds);

/**
 * 一组按顺序执行的步骤：顶层步骤，或某个分支、某次遍历里的步骤。
 */
export function StepList({
  drafts,
  path,
  depth,
  before,
  loop,
  ctx,
  onChange,
}: {
  drafts: DraftStep[];
  /** 与后端校验错误一致的路径前缀，例如 steps 或 steps[0].branches[1].steps。 */
  path: string;
  depth: number;
  /** 在这组步骤之前执行过的步骤，可以被引用。 */
  before: WorkflowStep[];
  loop: boolean;
  ctx: StepEditorContext;
  onChange: (drafts: DraftStep[]) => void;
}) {
  const move = (index: number, delta: -1 | 1) => {
    const next = [...drafts];
    const [moved] = next.splice(index, 1);
    next.splice(index + delta, 0, moved!);
    onChange(next);
  };

  return (
    <div className={cn('grid gap-3', depth > 0 && 'border-l-2 pl-3')}>
      {drafts.map((draft, index) => (
        <StepCard
          key={draft.uid}
          draft={draft}
          path={`${path}[${index}]`}
          number={index + 1}
          count={drafts.length}
          depth={depth}
          preceding={[...before, ...flatten(drafts.slice(0, index))]}
          loop={loop}
          ctx={ctx}
          onChange={(next) => onChange(drafts.map((d) => (d.uid === draft.uid ? next : d)))}
          onMove={(delta) => move(index, delta)}
          onRemove={() => onChange(drafts.filter((d) => d.uid !== draft.uid))}
        />
      ))}
      <div className='flex flex-wrap gap-2'>
        {kindsAt(depth).map((kind) => (
          <Button key={kind} type='button' variant='outline' size='sm' onClick={() => onChange([...drafts, newDraftStep(kind, ctx.newKey())])}>
            <Icons.add className='size-4' />
            {workflowStepKind[kind].label}
          </Button>
        ))}
      </div>
    </div>
  );
}

function StepCard({
  draft,
  path,
  number,
  count,
  depth,
  preceding,
  loop,
  ctx,
  onChange,
  onMove,
  onRemove,
}: {
  draft: DraftStep;
  path: string;
  number: number;
  count: number;
  depth: number;
  preceding: WorkflowStep[];
  loop: boolean;
  ctx: StepEditorContext;
  onChange: (draft: DraftStep) => void;
  onMove: (delta: -1 | 1) => void;
  onRemove: () => void;
}) {
  const { step } = draft;
  const { errors, actions, agents } = ctx;
  const kind = step.kind ?? 'action';
  const id = (field: string) => `${draft.uid}-${field}`;
  const set = (patch: Partial<WorkflowStep>) => onChange({ ...draft, step: { ...step, ...patch } });
  const action = actions?.find((a) => a.key === step.actionKey);
  const references = referencesFor(preceding, ctx.trigger, ctx.events, actions, loop);
  const kinds = kindsAt(depth).includes(kind) ? kindsAt(depth) : [...kindsAt(depth), kind];
  const setBranch = (uid: string, patch: Partial<DraftBranch>) => onChange({ ...draft, branches: draft.branches.map((b) => (b.uid === uid ? { ...b, ...patch } : b)) });

  return (
    <Card className={cn(depth > 0 && 'shadow-none')}>
      <CardHeader className='flex flex-row flex-wrap items-center gap-2 space-y-0 sm:gap-3'>
        <Badge variant='outline' className='font-mono'>
          {number}
        </Badge>
        <Input aria-label='步骤名称' className='max-w-xs min-w-0 flex-1' value={step.name} maxLength={100} placeholder='这一步做什么' onChange={(e) => set({ name: e.target.value })} />
        <Select
          value={kind}
          onValueChange={(value) => {
            const next = { ...draft, step: { ...step, kind: value as WorkflowStepKind } };
            onChange(value === 'branch' ? withDefaultBranches(next) : next);
          }}
        >
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
          <Button type='button' size='icon' variant='ghost' aria-label='上移' disabled={number === 1} onClick={() => onMove(-1)}>
            <Icons.arrowUp className='size-4' />
          </Button>
          <Button type='button' size='icon' variant='ghost' aria-label='下移' disabled={number === count} onClick={() => onMove(1)}>
            <Icons.arrowDown className='size-4' />
          </Button>
          <Button type='button' size='icon' variant='ghost' aria-label='删除步骤' onClick={onRemove}>
            <Icons.trash className='size-4' />
          </Button>
        </div>
      </CardHeader>
      <CardContent className='grid gap-4'>
        <FieldError errors={errors} path={`${path}.name`} />
        <FieldError errors={errors} path={`${path}.kind`} />
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
              <JsonTextarea id={id('input')} value={draft.inputText} invalid={!!errors[`${path}.input`]} onChange={(inputText) => onChange({ ...draft, inputText })} />
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
                <input type='checkbox' className='accent-primary size-4' checked={step.waitForCompletion ?? false} onChange={(e) => set({ waitForCompletion: e.target.checked })} />
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
            {step.delaySeconds && step.delaySeconds >= 60 ? <p className='text-muted-foreground text-xs'>约 {formatDuration(step.delaySeconds)}</p> : null}
            <FieldError errors={errors} path={`${path}.delaySeconds`} />
          </div>
        ) : null}

        {kind === 'http' ? (
          <>
            <div className='grid gap-2'>
              <Label htmlFor={id('url')}>请求地址</Label>
              <div className='flex gap-2'>
                <Select value={step.method ?? 'GET'} onValueChange={(value) => set({ method: value })}>
                  <SelectTrigger className='w-28 shrink-0' aria-label='请求方法'>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {httpMethods.map((m) => (
                      <SelectItem key={m} value={m}>
                        {m}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <Input id={id('url')} className='min-w-0 font-mono' value={step.url ?? ''} placeholder='https://example.com/hooks/{{ input.id }}' onChange={(e) => set({ url: e.target.value })} />
              </div>
              <FieldError errors={errors} path={`${path}.method`} />
              <FieldError errors={errors} path={`${path}.url`} />
            </div>
            <div className='grid gap-2'>
              <Label>请求头</Label>
              {draft.headers.map((header) => (
                <div key={header.uid} className='flex gap-2'>
                  <Input
                    aria-label='请求头名称'
                    className='w-40 shrink-0 font-mono sm:w-48'
                    value={header.name}
                    placeholder='Authorization'
                    onChange={(e) => onChange({ ...draft, headers: draft.headers.map((h) => (h.uid === header.uid ? { ...h, name: e.target.value } : h)) })}
                  />
                  <Input
                    aria-label='请求头的值'
                    className='min-w-0 font-mono'
                    value={header.value}
                    placeholder='Bearer …'
                    onChange={(e) => onChange({ ...draft, headers: draft.headers.map((h) => (h.uid === header.uid ? { ...h, value: e.target.value } : h)) })}
                  />
                  <Button type='button' size='icon' variant='ghost' aria-label='删除请求头' onClick={() => onChange({ ...draft, headers: draft.headers.filter((h) => h.uid !== header.uid) })}>
                    <Icons.close className='size-4' />
                  </Button>
                </div>
              ))}
              <div>
                <Button type='button' variant='ghost' size='sm' className='-ml-2' onClick={() => onChange({ ...draft, headers: [...draft.headers, { uid: nextUid(), name: '', value: '' }] })}>
                  <Icons.add className='size-4' />
                  添加请求头
                </Button>
              </div>
              <p className='text-muted-foreground text-xs'>请求头的值对能查看工作流的成员可见；运行记录里 Authorization、带 token / key 的请求头会打码。</p>
              <FieldError errors={errors} path={`${path}.headers`} />
            </div>
            {['GET', 'DELETE'].includes(step.method ?? 'GET') ? null : (
              <div className='grid gap-2'>
                <Label htmlFor={id('body')}>请求体（JSON）</Label>
                <JsonTextarea id={id('body')} value={draft.bodyText} invalid={!!errors[`${path}.body`]} placeholder='{ "title": "{{ trigger.task.title }}" }' onChange={(bodyText) => onChange({ ...draft, bodyText })} />
                <p className='text-muted-foreground text-xs'>对象和数组按 JSON 发送；要发送纯文本时写成带引号的字符串。</p>
                <FieldError errors={errors} path={`${path}.body`} />
              </div>
            )}
            <div className='flex items-center gap-2 text-sm'>
              <Label htmlFor={id('http-timeout')}>超时</Label>
              <Input
                id={id('http-timeout')}
                type='number'
                min={1}
                max={120}
                className='w-24'
                value={step.timeoutSeconds ?? ''}
                placeholder='30'
                onChange={(e) => set({ timeoutSeconds: e.target.value ? Number(e.target.value) : undefined })}
              />
              秒
            </div>
            <FieldError errors={errors} path={`${path}.timeoutSeconds`} />
          </>
        ) : null}

        {kind === 'branch' ? (
          <div className='grid gap-3'>
            {draft.branches.map((branch, b) => {
              const branchPath = `${path}.branches[${b}]`;
              const last = b === draft.branches.length - 1;
              return (
                <div key={branch.uid} className='grid gap-3 rounded-md border p-3'>
                  <div className='flex flex-wrap items-center gap-2'>
                    <Input aria-label='分支名称' className='w-32' value={branch.name} maxLength={50} onChange={(e) => setBranch(branch.uid, { name: e.target.value })} />
                    <Input
                      aria-label='分支条件'
                      className='min-w-48 flex-1 font-mono'
                      value={branch.when}
                      placeholder={last ? '留空表示其他情况' : '条件，例如 input.amount >= 10000'}
                      onChange={(e) => setBranch(branch.uid, { when: e.target.value })}
                    />
                    <Button
                      type='button'
                      size='icon'
                      variant='ghost'
                      aria-label='删除分支'
                      disabled={draft.branches.length === 1}
                      onClick={() => onChange({ ...draft, branches: draft.branches.filter((x) => x.uid !== branch.uid) })}
                    >
                      <Icons.close className='size-4' />
                    </Button>
                  </div>
                  <FieldError errors={errors} path={`${branchPath}.name`} />
                  <FieldError errors={errors} path={`${branchPath}.when`} />
                  <StepList
                    drafts={branch.steps}
                    path={`${branchPath}.steps`}
                    depth={depth + 1}
                    before={preceding}
                    loop={loop}
                    ctx={ctx}
                    onChange={(steps) => setBranch(branch.uid, { steps })}
                  />
                </div>
              );
            })}
            <div>
              <Button
                type='button'
                variant='ghost'
                size='sm'
                className='-ml-2'
                disabled={draft.branches.length >= MAX_BRANCHES}
                onClick={() => {
                  // 新分支插在兜底分支前面。
                  const fallback = draft.branches.at(-1);
                  const fresh = { uid: nextUid(), name: `分支 ${draft.branches.length}`, when: '', steps: [] };
                  const branches = fallback && !fallback.when.trim() ? [...draft.branches.slice(0, -1), fresh, fallback] : [...draft.branches, fresh];
                  onChange({ ...draft, branches });
                }}
              >
                <Icons.add className='size-4' />
                添加分支
              </Button>
            </div>
            <FieldError errors={errors} path={`${path}.branches`} />
          </div>
        ) : null}

        {kind === 'for_each' ? (
          <div className='grid gap-3'>
            <div className='grid gap-2'>
              <Label htmlFor={id('items')}>要遍历的列表</Label>
              <Input id={id('items')} className='font-mono' value={step.items ?? ''} placeholder='例如 steps.find.output.items 或 input.customers' onChange={(e) => set({ items: e.target.value })} />
              <p className='text-muted-foreground text-xs'>写一个结果为列表的表达式。下面的步骤对每一项执行一次，用 item 引用当前项、index 引用序号（从 0 开始）。</p>
              <FieldError errors={errors} path={`${path}.items`} />
            </div>
            <StepList drafts={draft.children} path={`${path}.steps`} depth={depth + 1} before={preceding} loop ctx={ctx} onChange={(children) => onChange({ ...draft, children })} />
            <FieldError errors={errors} path={`${path}.steps`} />
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
                <Input id={id('key')} className='font-mono' value={step.key} maxLength={40} onChange={(e) => set({ key: e.target.value })} aria-invalid={!!errors[`${path}.key`]} />
                <p className='text-muted-foreground text-xs'>后面的步骤用 steps.{step.key || '标识'}.output 引用本步结果。</p>
                <FieldError errors={errors} path={`${path}.key`} />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor={id('when')}>执行条件</Label>
                <Input
                  id={id('when')}
                  className='font-mono'
                  value={step.when ?? ''}
                  placeholder='留空表示总是执行，例如 input.amount > 1000'
                  onChange={(e) => set({ when: e.target.value || undefined })}
                />
                <FieldError errors={errors} path={`${path}.when`} />
              </div>
            </div>
            <label className='flex items-center gap-2 text-sm'>
              <input type='checkbox' className='accent-primary size-4' checked={step.continueOnError ?? false} onChange={(e) => set({ continueOnError: e.target.checked })} />
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

function JsonTextarea({ id, value, invalid, placeholder, onChange }: { id: string; value: string; invalid: boolean; placeholder?: string; onChange: (value: string) => void }) {
  return (
    <Textarea
      id={id}
      className='font-mono text-sm'
      rows={Math.min(14, Math.max(4, value.split('\n').length + 1))}
      value={value}
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value)}
      aria-invalid={invalid}
    />
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
