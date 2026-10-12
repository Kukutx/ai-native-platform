'use client';

import { useCallback, useRef, useState } from 'react';

/** 元素的当前宽度（像素），随容器尺寸变化更新；挂载前为 0。 */
export function useElementWidth<T extends HTMLElement>() {
  const [width, setWidth] = useState(0);
  const observer = useRef<ResizeObserver | null>(null);

  const ref = useCallback((node: T | null) => {
    observer.current?.disconnect();
    observer.current = null;
    if (!node) {
      return;
    }

    observer.current = new ResizeObserver(([entry]) => setWidth(Math.floor(entry.contentRect.width)));
    observer.current.observe(node);
    setWidth(Math.floor(node.getBoundingClientRect().width));
  }, []);

  return [ref, width] as const;
}
