import type { WorkflowStep, WorkflowStepKind } from '@ainative/api-client';

export type FieldErrors = Record<string, string[] | undefined>;

export interface DraftHeader {
  uid: string;
  name: string;
  value: string;
}

export interface DraftBranch {
  uid: string;
  name: string;
  when: string;
  steps: DraftStep[];
}

/**
 * 编辑中的步骤。JSON 字段按文本编辑、保存时再解析；子步骤（分支里的、遍历的）同样是编辑中的步骤，
 * 不放在 step 里，保存时再组装。
 */
export interface DraftStep {
  /** 编辑期间的稳定标识（步骤 key 可以被改）。 */
  uid: string;
  step: WorkflowStep;
  inputText: string;
  bodyText: string;
  headers: DraftHeader[];
  branches: DraftBranch[];
  children: DraftStep[];
}

let uidSeed = 0;
export const nextUid = () => `d${++uidSeed}`;

export const MAX_NESTING = 3;
export const MAX_BRANCHES = 10;

const json = (value: unknown) => (value === undefined || value === null ? '' : JSON.stringify(value, null, 2));

export function toDraftStep(step: WorkflowStep): DraftStep {
  const { branches, steps, ...rest } = step;
  return {
    uid: nextUid(),
    step: rest,
    inputText: json(step.input) || '{}',
    bodyText: json(step.body),
    headers: Object.entries(step.headers ?? {}).map(([name, value]) => ({ uid: nextUid(), name, value })),
    branches: (branches ?? []).map((b) => ({ uid: nextUid(), name: b.name, when: b.when ?? '', steps: (b.steps ?? []).map(toDraftStep) })),
    children: (steps ?? []).map(toDraftStep),
  };
}

export function newDraftStep(kind: WorkflowStepKind, key: string): DraftStep {
  const draft = toDraftStep({ key, name: '', kind });
  return kind === 'branch' ? withDefaultBranches(draft) : draft;
}

/** 新的条件分支先给两个分支：一个待填条件，一个兜底。 */
export function withDefaultBranches(draft: DraftStep): DraftStep {
  return draft.branches.length
    ? draft
    : {
        ...draft,
        branches: [
          { uid: nextUid(), name: '分支 1', when: '', steps: [] },
          { uid: nextUid(), name: '其他情况', when: '', steps: [] },
        ],
      };
}

/** 本步骤及其所有后代的定义（用于列出可引用的变量、检查标识是否重复）。 */
export function flatten(drafts: DraftStep[]): WorkflowStep[] {
  return drafts.flatMap((d) => [d.step, ...flatten(d.branches.flatMap((b) => b.steps)), ...flatten(d.children)]);
}

/** 整个工作流里没用过的步骤标识。 */
export function freeKey(drafts: DraftStep[]) {
  const used = new Set(flatten(drafts).map((s) => s.key));
  let n = used.size + 1;
  while (used.has(`step_${n}`)) {
    n++;
  }

  return `step_${n}`;
}

function parse(text: string, path: string, errors: FieldErrors) {
  try {
    return JSON.parse(text) as unknown;
  } catch {
    errors[path] = ['不是合法的 JSON。'];
    return undefined;
  }
}

/** 组装成提交的定义；JSON 解析错误按与后端相同的字段路径写进 errors。 */
export function fromDraftSteps(drafts: DraftStep[], path: string, errors: FieldErrors): WorkflowStep[] {
  return drafts.map((draft, index) => fromDraftStep(draft, `${path}[${index}]`, errors));
}

function fromDraftStep(draft: DraftStep, path: string, errors: FieldErrors): WorkflowStep {
  // 只带上当前类型用到的字段，切换类型后残留的旧字段不提交。
  const { key, name, kind = 'action', when, continueOnError } = draft.step;
  const base: WorkflowStep = { key, name, kind, when, continueOnError };
  const s = draft.step;
  switch (kind) {
    case 'action':
      return { ...base, actionKey: s.actionKey, input: draft.inputText.trim() ? parse(draft.inputText, `${path}.input`, errors) : {} };
    case 'agent_task':
      return { ...base, agentId: s.agentId, title: s.title, instructions: s.instructions, waitForCompletion: s.waitForCompletion, timeoutMinutes: s.timeoutMinutes };
    case 'llm':
      return { ...base, prompt: s.prompt };
    case 'delay':
      return { ...base, delaySeconds: s.delaySeconds };
    case 'http': {
      const headers = Object.fromEntries(draft.headers.filter((h) => h.name.trim()).map((h) => [h.name.trim(), h.value]));
      return {
        ...base,
        method: s.method ?? 'GET',
        url: s.url,
        headers: Object.keys(headers).length ? headers : undefined,
        body: draft.bodyText.trim() ? parse(draft.bodyText, `${path}.body`, errors) : undefined,
        timeoutSeconds: s.timeoutSeconds,
      };
    }
    case 'branch':
      return {
        ...base,
        branches: draft.branches.map((b, i) => ({ name: b.name, when: b.when.trim() || undefined, steps: fromDraftSteps(b.steps, `${path}.branches[${i}].steps`, errors) })),
      };
    case 'for_each':
      return { ...base, items: s.items, steps: fromDraftSteps(draft.children, `${path}.steps`, errors) };
    default:
      return base;
  }
}
