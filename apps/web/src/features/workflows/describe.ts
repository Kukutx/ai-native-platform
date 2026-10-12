import type { ActionDescriptorDto, WorkflowEventDto, WorkflowStep, WorkflowTriggerDto } from '@ainative/api-client';

/** 编辑器里常用的定时规则，选中后直接填入 cron。 */
export const cronPresets = [
  { cron: '0 9 * * *', label: '每天 9:00' },
  { cron: '0 9 * * MON-FRI', label: '工作日 9:00' },
  { cron: '0 9 * * MON', label: '每周一 9:00' },
  { cron: '0 9 1 * *', label: '每月 1 日 9:00' },
  { cron: '0 * * * *', label: '每小时整点' },
  { cron: '*/15 * * * *', label: '每 15 分钟' },
];

export function describeTrigger(trigger: WorkflowTriggerDto, events?: WorkflowEventDto[]) {
  switch (trigger.kind ?? 'manual') {
    case 'event': {
      const name = events?.find((e) => e.key === trigger.eventType)?.name ?? trigger.eventType ?? '未选择事件';
      return trigger.filter ? `${name}（${trigger.filter}）` : name;
    }
    case 'schedule': {
      const preset = cronPresets.find((p) => p.cron === trigger.cron)?.label ?? trigger.cron ?? '';
      return `${preset} · ${trigger.timeZone || 'UTC'}`;
    }
    default:
      return '手动运行';
  }
}

export interface Reference {
  expr: string;
  hint: string;
}

/**
 * 某一步可以引用的变量：运行参数、触发数据、前面步骤的输出。
 * 写进模板时用 {{ expr }}，写进 when 条件时直接写 expr。
 */
export function referencesFor(
  index: number,
  steps: WorkflowStep[],
  trigger: WorkflowTriggerDto,
  events: WorkflowEventDto[] | undefined,
  actions: ActionDescriptorDto[] | undefined,
): Reference[] {
  const refs: Reference[] = [{ expr: 'input.字段名', hint: '手动运行时填写的参数' }];
  switch (trigger.kind ?? 'manual') {
    case 'event': {
      const event = events?.find((e) => e.key === trigger.eventType);
      refs.push({ expr: 'trigger.actor.name', hint: '引发事件的成员或智能体' });
      if (event) {
        refs.push({ expr: 'trigger.…', hint: event.description });
      }
      break;
    }
    case 'schedule':
      refs.push({ expr: 'trigger.scheduledAt', hint: '本次计划运行的时间' });
      break;
    default:
      refs.push({ expr: 'trigger.actor.name', hint: '点击运行的人' });
  }

  for (const step of steps.slice(0, index)) {
    const base = `steps.${step.key}`;
    refs.push({ expr: `${base}.status`, hint: `「${step.name}」的结果：succeeded / failed / skipped` });
    switch (step.kind ?? 'action') {
      case 'llm':
        refs.push({ expr: `${base}.output.text`, hint: `「${step.name}」生成的文本` });
        refs.push({ expr: `${base}.output.json.字段名`, hint: '模型输出是 JSON 时可直接取字段' });
        break;
      case 'agent_task':
        refs.push({ expr: `${base}.output.taskId`, hint: `「${step.name}」创建的任务 ID` });
        if (step.waitForCompletion) {
          refs.push({ expr: `${base}.output.result`, hint: '智能体写在评论里的结论' });
        }
        break;
      case 'delay':
        refs.push({ expr: `${base}.output.waitedSeconds`, hint: '等待的秒数' });
        break;
      default: {
        const action = actions?.find((a) => a.key === step.actionKey);
        refs.push({ expr: `${base}.output.字段名`, hint: action ? `「${action.title}」返回的数据，例如创建任务后的 id` : '动作返回的数据' });
      }
    }
  }

  return refs;
}

interface SchemaProperty {
  name: string;
  type: string;
  description?: string;
  required: boolean;
}

type Schema = { type?: string | string[]; properties?: Record<string, Schema>; required?: string[]; description?: string; enum?: unknown[] };

function typeOf(schema: Schema) {
  const types = (Array.isArray(schema.type) ? schema.type : [schema.type]).filter((t): t is string => !!t && t !== 'null');
  if (schema.enum?.length) {
    return schema.enum.filter((v) => v !== null).join(' | ');
  }

  return types[0] ?? 'any';
}

/** 读出动作输入的字段说明，编辑器里提示用户怎么填。 */
export function schemaProperties(inputSchema: unknown): SchemaProperty[] {
  const schema = (inputSchema ?? {}) as Schema;
  const required = new Set(schema.required ?? []);
  return Object.entries(schema.properties ?? {}).map(([name, prop]) => ({
    name,
    type: typeOf(prop),
    description: prop.description,
    required: required.has(name),
  }));
}

/** 按动作的输入结构生成必填字段的骨架，减少手写 JSON。 */
export function inputSkeleton(inputSchema: unknown): string {
  const skeleton: Record<string, unknown> = {};
  for (const prop of schemaProperties(inputSchema).filter((p) => p.required)) {
    skeleton[prop.name] =
      prop.type === 'number' || prop.type === 'integer'
        ? 0
        : prop.type === 'boolean'
          ? false
          : prop.type === 'array'
            ? []
            : prop.type === 'object'
              ? {}
              : '';
  }

  return JSON.stringify(skeleton, null, 2);
}
