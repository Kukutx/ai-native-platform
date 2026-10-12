'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'next/navigation';

type UrlValue = string | number | boolean;

/** 可以放进地址的状态：每一项的默认值决定它的类型（文本、数字或开关）。 */
export type UrlStateShape = Record<string, UrlValue>;

/** 默认值写成字面量（例如 false、''）时按对应的基本类型处理。 */
type Widen<T extends UrlStateShape> = { [K in keyof T]: T[K] extends string ? string : T[K] extends number ? number : boolean };

function parse<T extends UrlStateShape>(params: URLSearchParams, defaults: T): T {
  const state: UrlStateShape = { ...defaults };
  for (const [key, fallback] of Object.entries(defaults)) {
    const raw = params.get(key);
    if (raw === null) {
      continue;
    }

    if (typeof fallback === 'number') {
      const value = Number.parseInt(raw, 10);
      state[key] = Number.isFinite(value) ? value : fallback;
    } else if (typeof fallback === 'boolean') {
      state[key] = raw === '1' || raw === 'true';
    } else {
      state[key] = raw;
    }
  }

  return state as T;
}

function serialize(value: UrlValue) {
  return typeof value === 'boolean' ? (value ? '1' : '0') : String(value);
}

/**
 * 列表页的筛选条件和页码记在地址的查询参数里：刷新页面、分享链接、从详情页返回时都保持不变。
 * 用法：`const [state, setState] = useUrlState({ q: '', page: 1 })`，改筛选条件时一并传 `page: 1` 回到第一页。
 *
 * - 等于默认值的项不写进地址，地址里其他参数（例如别的组件的标签页）原样保留。
 * - 修改用 history.replaceState：不产生新的历史记录（返回键直接回到上一个页面），也不重新加载数据之外的内容。
 * - 地址里的值可能被手工改坏，枚举类的值由调用方校验后再用。
 * - 内部使用 useSearchParams，调用它的组件要包在 `<Suspense>` 里，静态导出（NEXT_OUTPUT=export）才能通过。
 */
export function useUrlState<T extends UrlStateShape>(defaults: T): [Widen<T>, (patch: Partial<Widen<T>>) => void] {
  const params = useSearchParams();
  // 调用方通常每次渲染都传一个新的对象字面量，按内容比较。
  const defaultsKey = JSON.stringify(defaults);
  const query = params.toString();
  const state = useMemo(() => parse(new URLSearchParams(query), JSON.parse(defaultsKey) as Widen<T>), [query, defaultsKey]);

  const setState = useCallback(
    (patch: Partial<Widen<T>>) => {
      const fallback = JSON.parse(defaultsKey) as Widen<T>;
      // 以浏览器当前地址为准：同一轮事件里连续修改多次时不会互相覆盖。
      const next = new URLSearchParams(window.location.search);
      for (const [key, value] of Object.entries(patch)) {
        if (value === undefined || value === fallback[key] || value === '') {
          next.delete(key);
        } else {
          next.set(key, serialize(value));
        }
      }

      const search = next.toString();
      const url = `${window.location.pathname}${search ? `?${search}` : ''}${window.location.hash}`;
      if (url !== `${window.location.pathname}${window.location.search}${window.location.hash}`) {
        // Next.js 会同步 useSearchParams，不触发页面跳转。
        window.history.replaceState(null, '', url);
      }
    },
    [defaultsKey],
  );

  return [state, setState];
}

/**
 * 搜索框：输入时立即显示，停顿片刻后才提交（写进地址、发起查询），避免每敲一个字就请求一次。
 * 地址里的值从别处改变（例如返回上一页、清空筛选）时，输入框跟着更新。
 * 返回 [输入框的值, 输入框的 onChange 处理]。
 */
export function useSearchText(value: string, commit: (text: string) => void, delay = 300): [string, (text: string) => void] {
  const [text, setText] = useState(value);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  // 最近一次提交的内容：地址更新成它时不必回写输入框（用户可能已经接着输入了）。
  const sent = useRef(value);
  const commitRef = useRef(commit);

  useEffect(() => {
    commitRef.current = commit;
  });

  useEffect(() => {
    if (value !== sent.current) {
      sent.current = value;
      clearTimeout(timer.current);
      setText(value);
    }
  }, [value]);

  useEffect(() => () => clearTimeout(timer.current), []);

  const onChange = useCallback(
    (next: string) => {
      setText(next);
      clearTimeout(timer.current);
      timer.current = setTimeout(() => {
        const trimmed = next.trim();
        sent.current = trimmed;
        commitRef.current(trimmed);
      }, delay);
    },
    [delay],
  );

  return [text, onChange];
}

/**
 * 地址里的值是给定选项之一时返回它，否则返回 undefined。地址可能被手工改坏或来自旧版本，枚举类的筛选条件先过一遍。
 * options 可以是选项数组，也可以是以选项为键的对象（例如 lib/labels 里的状态表）。
 */
export function pickOption<K extends string>(value: string, options: readonly K[] | Record<K, unknown>): K | undefined {
  const known = Array.isArray(options) ? options.includes(value as K) : Object.hasOwn(options, value);
  return known ? (value as K) : undefined;
}
