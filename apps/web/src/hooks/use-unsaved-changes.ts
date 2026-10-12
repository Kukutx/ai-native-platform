'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { useConfirm, type ConfirmOptions } from '@/components/app/confirm-provider';

const leaveConfirm: ConfirmOptions = {
  title: '放弃未保存的修改？',
  description: '这里有修改还没有保存，离开后会丢失。',
  confirmLabel: '离开',
  cancelLabel: '继续编辑',
};

/** 当前页面的后退、前进拦截；为 null 时 popstate 照常交给 Next.js。 */
let historyBlocker: ((event: PopStateEvent) => void) | null = null;
let historyGuardInstalled = false;

/**
 * 在应用启动时调用一次（见 Providers）。window 上的监听按注册顺序执行（捕获阶段也一样），
 * Next.js 在应用挂载后才注册自己的 popstate 监听，所以这里必须更早注册，才能在页面切换之前拦下后退、前进。
 */
export function installHistoryGuard() {
  if (historyGuardInstalled || typeof window === 'undefined') {
    return;
  }

  historyGuardInstalled = true;
  window.addEventListener('popstate', (event) => historyBlocker?.(event));
}

/**
 * 有未保存的修改时拦截离开：关闭、刷新标签页或打开站外地址交给浏览器自带的提示（beforeunload）；
 * 站内链接和浏览器的后退、前进先弹出确认框。App Router 没有可以取消的路由事件，所以抢在 Next.js 之前处理链接点击和 popstate。
 * 代码里主动调用的 router.push（例如保存成功后跳到详情页）不受影响。
 */
export function useUnsavedChangesGuard(dirty: boolean) {
  const router = useRouter();
  const confirm = useConfirm();

  useEffect(() => {
    if (!dirty) {
      return;
    }

    // 编辑页自己的历史记录：取消后退时用它回到原地。
    const here = { state: window.history.state as unknown, url: window.location.href };
    let replaying = false;

    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // 部分浏览器还要求设置 returnValue 才会提示。
      event.returnValue = '';
    };

    const onClick = (event: MouseEvent) => {
      if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
        return;
      }

      const anchor = event.target instanceof Element ? event.target.closest('a[href]') : null;
      if (!(anchor instanceof HTMLAnchorElement) || (anchor.target && anchor.target !== '_self') || anchor.hasAttribute('download')) {
        return;
      }

      const url = new URL(anchor.href, window.location.href);
      // 站外地址会卸载页面，由 beforeunload 提示；同一页面内的锚点不算离开。
      if (url.origin !== window.location.origin || (url.pathname === window.location.pathname && url.search === window.location.search)) {
        return;
      }

      // 只阻止跳转，不阻止冒泡：链接上的其他点击处理（例如手机上收起侧栏）照常执行，Next.js 的 Link 见到 defaultPrevented 会放弃导航。
      event.preventDefault();
      void confirm(leaveConfirm).then((ok) => {
        if (ok) {
          router.push(`${url.pathname}${url.search}${url.hash}`);
        }
      });
    };

    const onPopState = (event: PopStateEvent) => {
      if (replaying) {
        return;
      }

      // 地址栏此时已经是目标地址；先不让 Next.js 切换页面，确认后再把这次 popstate 交给它，取消则回到编辑页的地址。
      event.stopImmediatePropagation();
      void confirm(leaveConfirm).then((ok) => {
        if (ok) {
          replaying = true;
          window.dispatchEvent(new PopStateEvent('popstate', { state: window.history.state as unknown }));
          replaying = false;
        } else {
          window.history.pushState(here.state, '', here.url);
        }
      });
    };

    window.addEventListener('beforeunload', onBeforeUnload);
    // 捕获阶段先于 React 和 Next.js 的 Link 收到点击。
    window.addEventListener('click', onClick, true);
    historyBlocker = onPopState;
    return () => {
      window.removeEventListener('beforeunload', onBeforeUnload);
      window.removeEventListener('click', onClick, true);
      if (historyBlocker === onPopState) {
        historyBlocker = null;
      }
    };
  }, [dirty, confirm, router]);
}
