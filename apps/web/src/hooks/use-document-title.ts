'use client';

import { useEffect } from 'react';
import { PRODUCT_NAME } from '@/lib/config';

/** 当前页面要显示的标题；没有页面设置时为 null。 */
let wanted: string | null = null;
let observer: MutationObserver | null = null;

function apply() {
  if (wanted !== null && document.title !== wanted) {
    document.title = wanted;
  }
}

/**
 * 设置浏览器标签页标题，形如「任务 · AI 原生工作台」；title 为空（例如记录还在加载）时只显示产品名。
 * 页面都是客户端组件，静态导出后也没有服务端，所以在客户端设置 document.title。
 */
export function useDocumentTitle(title: string | null | undefined) {
  useEffect(() => {
    wanted = title ? `${title} · ${PRODUCT_NAME}` : PRODUCT_NAME;
    apply();
    // 页面切换时 Next.js 会重新插入根布局元数据里的 <title>，而且排在最前面（浏览器以第一个为准），插入后要再写一次。
    observer ??= new MutationObserver(apply);
    observer.observe(document.head, { childList: true, subtree: true, characterData: true });
    return () => {
      // 恢复产品名，避免下一个页面在设置自己的标题之前显示上一个页面的标题。
      wanted = null;
      document.title = PRODUCT_NAME;
    };
  }, [title]);
}
