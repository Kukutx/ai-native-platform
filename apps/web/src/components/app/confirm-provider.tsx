'use client';

import { createContext, useCallback, useContext, useRef, useState } from 'react';
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog';
import { buttonVariants } from '@/components/ui/button';

export interface ConfirmOptions {
  /** 要确认的事，写成问句，例如「删除客户「星火科技」？」。 */
  title: string;
  /** 后果说明，例如哪些数据会一并删除、能否恢复。 */
  description?: React.ReactNode;
  /** 确认按钮的文字，默认「确定」；尽量写成具体动作，例如「删除」「终止」。 */
  confirmLabel?: string;
  cancelLabel?: string;
  /** 不可撤销的危险操作用红色按钮，默认是。 */
  destructive?: boolean;
}

type Confirm = (options: ConfirmOptions) => Promise<boolean>;

const ConfirmContext = createContext<Confirm | null>(null);

/**
 * 替代浏览器原生 confirm() 的确认框：样式与界面一致，桌面端（Tauri）和移动浏览器里也能正常显示。
 */
export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const [open, setOpen] = useState(false);
  // 关闭动画期间保留内容，避免对话框在淡出时变空。
  const [options, setOptions] = useState<ConfirmOptions | null>(null);
  const resolver = useRef<((confirmed: boolean) => void) | null>(null);

  const settle = useCallback((confirmed: boolean) => {
    resolver.current?.(confirmed);
    resolver.current = null;
    setOpen(false);
  }, []);

  const confirm = useCallback<Confirm>((next) => {
    // 上一个确认还没处理完又发起新的：按取消处理上一个。
    resolver.current?.(false);
    setOptions(next);
    setOpen(true);
    return new Promise<boolean>((resolve) => {
      resolver.current = resolve;
    });
  }, []);

  const destructive = options?.destructive ?? true;

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <AlertDialog open={open} onOpenChange={(next) => !next && settle(false)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>{options?.title}</AlertDialogTitle>
            {options?.description ? <AlertDialogDescription>{options.description}</AlertDialogDescription> : null}
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>{options?.cancelLabel ?? '取消'}</AlertDialogCancel>
            <AlertDialogAction className={buttonVariants({ variant: destructive ? 'destructive' : 'default' })} onClick={() => settle(true)}>
              {options?.confirmLabel ?? '确定'}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </ConfirmContext.Provider>
  );
}

/**
 * 弹出确认框，用户确认时返回 true。用法：`confirm({ title, description, confirmLabel }).then((ok) => ok && remove.mutate())`。
 */
export function useConfirm() {
  const context = useContext(ConfirmContext);
  if (!context) {
    throw new Error('useConfirm must be used within ConfirmProvider');
  }

  return context;
}
