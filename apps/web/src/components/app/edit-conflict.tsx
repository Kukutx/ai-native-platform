'use client';

import { useRef, useState } from 'react';
import { toast } from 'sonner';
import { toProblem } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { errorMessage } from '@/lib/errors';

/** 编辑期间记录被别人（或智能体）改过：后端返回 409，code 形如 crm_company.modified。 */
export function isEditConflict(error: unknown) {
  const problem = toProblem(error);
  return problem.status === 409 && problem.code.endsWith('.modified');
}

export interface EditVersion<T> {
  /** 保存时提交的 expectedUpdatedAt：打开表单（或载入最新）时的数据版本，不随后台刷新变化。 */
  expected: () => string | undefined;
  /** 最近一次保存是否因为冲突被拒绝。 */
  conflict: boolean;
  /** 正在取最新数据。 */
  busy: boolean;
  /** 放进 useMutation 的 onError：识别冲突并显示处理选项。 */
  onError: (error: unknown) => void;
  /** 放弃本地修改：取最新数据交给 apply（通常是用它重新初始化表单）。 */
  loadLatest: (apply: (latest: T) => void) => Promise<void>;
  /** 仍然覆盖：以最新版本号为基准重新提交本地内容。 */
  overwrite: (resubmit: () => void) => Promise<void>;
}

/**
 * 编辑表单的乐观并发控制。实时通道会在别人修改后刷新页面数据，如果直接提交最新的 updatedAt，
 * 一张打开已久的表单就会悄悄覆盖别人的修改；这里记住打开表单时的版本，冲突时保留用户的修改，由用户决定怎么处理。
 * meta 里配合 `handledError: isEditConflict`，冲突不再弹出全局错误提示。
 * base 用于表单晚于页面挂载的情况（例如保存报表的对话框）：传入页面开始编辑时记下的版本。
 */
export function useEditVersion<T extends { updatedAt: string }>(entity: T | null | undefined, load: () => Promise<T>, base?: string): EditVersion<T> {
  const version = useRef(base ?? entity?.updatedAt);
  const [conflict, setConflict] = useState(false);
  const [busy, setBusy] = useState(false);

  const fetchLatest = async () => {
    setBusy(true);
    try {
      const latest = await load();
      version.current = latest.updatedAt;
      setConflict(false);
      return latest;
    } catch (error) {
      toast.error(`读取最新内容失败：${errorMessage(error)}`);
      return null;
    } finally {
      setBusy(false);
    }
  };

  return {
    expected: () => version.current,
    conflict,
    busy,
    onError: (error) => {
      if (isEditConflict(error)) {
        setConflict(true);
      }
    },
    loadLatest: async (apply) => {
      const latest = await fetchLatest();
      if (latest) {
        apply(latest);
      }
    },
    overwrite: async (resubmit) => {
      if (await fetchLatest()) {
        resubmit();
      }
    },
  };
}

/** 保存冲突时的提示与两个选择：载入最新（放弃本地修改）或仍然覆盖。 */
export function EditConflictAlert<T>({
  edit,
  subject,
  onLoadLatest,
  onOverwrite,
  disabled,
}: {
  edit: EditVersion<T>;
  /** 记录名称，例如「客户」「工作流」。 */
  subject: string;
  onLoadLatest: (latest: T) => void;
  onOverwrite: () => void;
  disabled?: boolean;
}) {
  if (!edit.conflict) {
    return null;
  }

  const busy = edit.busy || disabled;
  return (
    <Alert className='border-warning/50 bg-warning/10'>
      <Icons.warning className='text-warning' />
      <AlertTitle className='line-clamp-none'>{subject}在你编辑期间被其他人修改过，这次没有保存</AlertTitle>
      <AlertDescription>
        <p>可以载入最新内容（放弃你在这里的修改），或者仍然用你的内容覆盖对方的修改。</p>
        <div className='flex flex-wrap gap-2 pt-1'>
          <Button type='button' size='sm' variant='outline' disabled={busy} onClick={() => void edit.loadLatest(onLoadLatest)}>
            {edit.busy ? <Icons.spinner className='animate-spin' /> : <Icons.refresh />}
            载入最新
          </Button>
          <Button type='button' size='sm' variant='destructive' disabled={busy} onClick={() => void edit.overwrite(onOverwrite)}>
            仍然覆盖
          </Button>
        </div>
      </AlertDescription>
    </Alert>
  );
}

/**
 * 对话框里编辑表单的外层：表单「载入最新」时用最新数据重新挂载表单（自定义字段、明细等子状态一并重置）。
 * 随对话框关闭一起卸载，下次打开仍从调用方传入的数据开始。
 */
export function ReloadableForm<T extends { id: string }>({
  entity,
  children,
}: {
  entity: T | null;
  children: (entity: T | null, reload: (latest: T) => void, key: string) => React.ReactNode;
}) {
  const [reloaded, setReloaded] = useState<{ entity: T; revision: number } | null>(null);
  const current = reloaded && reloaded.entity.id === entity?.id ? reloaded.entity : entity;
  return children(
    current,
    (latest) => setReloaded((previous) => ({ entity: latest, revision: (previous?.revision ?? 0) + 1 })),
    `${entity?.id ?? 'new'}:${reloaded?.revision ?? 0}`,
  );
}
