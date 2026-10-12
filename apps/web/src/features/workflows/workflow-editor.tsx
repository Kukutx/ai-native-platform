'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { toProblem, type SaveWorkflowRequest, type WorkflowDto, type WorkflowTriggerDto, type WorkflowTriggerKind } from '@ainative/api-client';
import { EditConflictAlert, isEditConflict, useEditVersion } from '@/components/app/edit-conflict';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { catalogQueries } from '@/lib/catalogs';
import { localTimeZone } from '@/lib/dates';
import { workflowTriggerKind } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cronPresets } from './describe';
import { freeKey, fromDraftSteps, toDraftStep, type DraftStep, type FieldErrors } from './draft';
import { FieldError, StepList } from './step-card';

interface Draft {
  name: string;
  description: string;
  isEnabled: boolean;
  trigger: WorkflowTriggerDto;
  steps: DraftStep[];
}

function toDraft(workflow?: WorkflowDto): Draft {
  if (!workflow) {
    return { name: '', description: '', isEnabled: true, trigger: { kind: 'manual' }, steps: [] };
  }

  return {
    name: workflow.name,
    description: workflow.description,
    isEnabled: workflow.isEnabled,
    trigger: { ...workflow.trigger },
    steps: workflow.steps.map(toDraftStep),
  };
}

const triggerKinds = Object.keys(workflowTriggerKind) as WorkflowTriggerKind[];
const NO_PRESET = '__custom__';

export function WorkflowEditor({ workflow }: { workflow?: WorkflowDto }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const { session } = useSession();
  const [draft, setDraft] = useState<Draft>(() => toDraft(workflow));
  const [errors, setErrors] = useState<FieldErrors>({});
  // 打开编辑器时的版本：后台刷新拿到别人保存的新版本后，不能用它悄悄覆盖对方的修改。
  const edit = useEditVersion(workflow, () => {
    const id = workflow!.id;
    return queryClient.fetchQuery({ queryKey: queryKeys.workflow(id), queryFn: ({ signal }) => api.workflows.get(id, signal), staleTime: 0 });
  });
  const catalog = useQuery({ queryKey: queryKeys.workflowCatalog, queryFn: ({ signal }) => api.workflows.catalog(signal) });
  const actions = useQuery(catalogQueries.actions());
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal) });

  const events = catalog.data?.events;
  const setTrigger = (patch: Partial<WorkflowTriggerDto>) => setDraft((d) => ({ ...d, trigger: { ...d.trigger, ...patch } }));

  const save = useMutation({
    mutationFn: (request: SaveWorkflowRequest) => (workflow ? api.workflows.update(workflow.id, request) : api.workflows.create(request)),
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.workflows });
      toast.success(workflow ? '已保存' : '工作流已创建');
      router.push(`/workflows/view?id=${saved.id}`);
    },
    meta: { handledError: isEditConflict },
    onError: (error) => {
      setErrors(toProblem(error).errors ?? {});
      // 期间被别人修改过：保留用户的草稿，由用户选择载入最新或仍然覆盖。
      edit.onError(error);
    },
  });

  const submit = () => {
    const local: FieldErrors = {};
    const payload = fromDraftSteps(draft.steps, 'steps', local);
    setErrors(local);
    if (Object.keys(local).length) {
      return;
    }

    const trigger: WorkflowTriggerDto =
      draft.trigger.kind === 'event'
        ? { kind: 'event', eventType: draft.trigger.eventType, filter: draft.trigger.filter || undefined }
        : draft.trigger.kind === 'schedule'
          ? { kind: 'schedule', cron: draft.trigger.cron, timeZone: draft.trigger.timeZone || 'UTC' }
          : draft.trigger.kind === 'webhook'
            ? { kind: 'webhook', filter: draft.trigger.filter || undefined }
            : { kind: 'manual' };
    save.mutate({
      name: draft.name,
      description: draft.description,
      isEnabled: draft.isEnabled,
      trigger,
      steps: payload,
      expectedUpdatedAt: edit.expected(),
    });
  };

  const ownerChanges = workflow && !(workflow.owner.kind === 'user' && workflow.owner.id === session?.user.id);
  const event = events?.find((e) => e.key === draft.trigger.eventType);

  return (
    <form
      className='grid gap-6'
      onSubmit={(e) => {
        e.preventDefault();
        submit();
      }}
    >
      <Card>
        <CardHeader>
          <CardTitle>基本信息</CardTitle>
        </CardHeader>
        <CardContent className='grid gap-4'>
          <div className='grid gap-2'>
            <Label htmlFor='wf-name'>名称</Label>
            <Input id='wf-name' value={draft.name} maxLength={100} required onChange={(e) => setDraft((d) => ({ ...d, name: e.target.value }))} placeholder='例如：新客户跟进' />
            <FieldError errors={errors} path='name' />
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='wf-description'>说明</Label>
            <Textarea
              id='wf-description'
              rows={2}
              value={draft.description}
              onChange={(e) => setDraft((d) => ({ ...d, description: e.target.value }))}
              placeholder='什么时候运行、做什么、为什么'
            />
            <FieldError errors={errors} path='description' />
          </div>
          <label className='flex items-center gap-2 text-sm'>
            <input type='checkbox' className='accent-primary size-4' checked={draft.isEnabled} onChange={(e) => setDraft((d) => ({ ...d, isEnabled: e.target.checked }))} />
            启用（停用后不会被事件或定时触发，仍可手动运行）
          </label>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>触发方式</CardTitle>
          <CardDescription>工作流以最后保存它的人的身份和权限运行。</CardDescription>
        </CardHeader>
        <CardContent className='grid gap-4'>
          <div className='flex flex-wrap gap-2'>
            {triggerKinds.map((kind) => (
              <Button
                key={kind}
                type='button'
                size='sm'
                variant={(draft.trigger.kind ?? 'manual') === kind ? 'default' : 'outline'}
                onClick={() => setTrigger(kind === 'schedule' && !draft.trigger.timeZone ? { kind, timeZone: localTimeZone() ?? 'UTC', cron: draft.trigger.cron ?? '0 9 * * *' } : { kind })}
              >
                {workflowTriggerKind[kind]}
              </Button>
            ))}
          </div>

          {draft.trigger.kind === 'event' ? (
            <>
              <div className='grid gap-2'>
                <Label>事件</Label>
                <Select value={draft.trigger.eventType ?? ''} onValueChange={(value) => setTrigger({ eventType: value })}>
                  <SelectTrigger className='w-full sm:w-96'>
                    <SelectValue placeholder='选择触发事件' />
                  </SelectTrigger>
                  <SelectContent>
                    {events?.map((e) => (
                      <SelectItem key={e.key} value={e.key}>
                        {e.name} <span className='text-muted-foreground font-mono text-xs'>{e.key}</span>
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                {event ? <p className='text-muted-foreground text-xs'>{event.description}</p> : null}
                <FieldError errors={errors} path='trigger.eventType' />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor='wf-filter'>过滤条件</Label>
                <Input
                  id='wf-filter'
                  className='font-mono'
                  value={draft.trigger.filter ?? ''}
                  onChange={(e) => setTrigger({ filter: e.target.value })}
                  placeholder="可选，例如 trigger.to == 'done'"
                />
                <FieldError errors={errors} path='trigger.filter' />
              </div>
            </>
          ) : null}

          {draft.trigger.kind === 'schedule' ? (
            <div className='grid gap-4 sm:grid-cols-3'>
              <div className='grid gap-2'>
                <Label>常用</Label>
                <Select value={cronPresets.some((p) => p.cron === draft.trigger.cron) ? draft.trigger.cron : NO_PRESET} onValueChange={(value) => value !== NO_PRESET && setTrigger({ cron: value })}>
                  <SelectTrigger className='w-full'>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {cronPresets.map((p) => (
                      <SelectItem key={p.cron} value={p.cron}>
                        {p.label}
                      </SelectItem>
                    ))}
                    <SelectItem value={NO_PRESET}>自定义</SelectItem>
                  </SelectContent>
                </Select>
              </div>
              <div className='grid gap-2'>
                <Label htmlFor='wf-cron'>cron（分 时 日 月 周）</Label>
                <Input id='wf-cron' className='font-mono' value={draft.trigger.cron ?? ''} onChange={(e) => setTrigger({ cron: e.target.value })} />
                <FieldError errors={errors} path='trigger.cron' />
              </div>
              <div className='grid gap-2'>
                <Label htmlFor='wf-tz'>时区</Label>
                <Input id='wf-tz' value={draft.trigger.timeZone ?? ''} onChange={(e) => setTrigger({ timeZone: e.target.value })} placeholder='Asia/Shanghai' />
                <FieldError errors={errors} path='trigger.timeZone' />
              </div>
            </div>
          ) : null}

          {draft.trigger.kind === 'webhook' ? (
            <>
              <p className='text-muted-foreground text-sm'>
                保存后在详情页复制专属地址，交给表单、代码仓库、支付回调等外部系统，用 POST 调用即可触发。请求体、查询参数和请求头分别是 trigger.body、trigger.query、trigger.headers。
              </p>
              <div className='grid gap-2'>
                <Label htmlFor='wf-webhook-filter'>过滤条件</Label>
                <Input
                  id='wf-webhook-filter'
                  className='font-mono'
                  value={draft.trigger.filter ?? ''}
                  onChange={(e) => setTrigger({ filter: e.target.value })}
                  placeholder="可选，例如 trigger.body.action == 'opened'"
                />
                <FieldError errors={errors} path='trigger.filter' />
              </div>
            </>
          ) : null}

          {(draft.trigger.kind ?? 'manual') === 'manual' ? (
            <p className='text-muted-foreground text-sm'>在列表或详情页点击“运行”启动，也可以让智能体在对话里运行它。</p>
          ) : null}
        </CardContent>
      </Card>

      <div className='grid gap-4'>
        <div className='flex flex-wrap items-center justify-between gap-2'>
          <h2 className='text-lg font-semibold'>步骤</h2>
          <FieldError errors={errors} path='steps' />
        </div>
        <StepList
          drafts={draft.steps}
          path='steps'
          depth={0}
          before={[]}
          loop={false}
          ctx={{ trigger: draft.trigger, events, actions: actions.data, agents: agents.data, errors, newKey: () => freeKey(draft.steps) }}
          onChange={(steps: DraftStep[]) => setDraft((d) => ({ ...d, steps }))}
        />
      </div>

      {ownerChanges ? (
        <Alert>
          <Icons.info className='size-4' />
          <AlertDescription>
            这个工作流目前以「{workflow.owner.name ?? '其他身份'}」的身份运行，保存后改为以你的身份和权限运行。
          </AlertDescription>
        </Alert>
      ) : null}

      <EditConflictAlert
        edit={edit}
        subject='工作流'
        onLoadLatest={(latest) => {
          setDraft(toDraft(latest));
          setErrors({});
        }}
        onOverwrite={submit}
        disabled={save.isPending}
      />

      <div className='flex justify-end gap-2'>
        <Button type='button' variant='outline' onClick={() => router.back()}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || draft.steps.length === 0}>
          {workflow ? '保存' : '创建'}
        </Button>
      </div>
    </form>
  );
}
