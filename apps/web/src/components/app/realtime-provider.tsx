'use client';

import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { getRealtime } from '@/lib/api';
import { invalidationMap } from '@/lib/query-keys';
import { useSession } from './session-provider';

/** 按查询键第一段限流的刷新间隔（毫秒）。 */
const THROTTLED: Record<string, number> = { analytics: 10_000 };

/**
 * 登录后连接实时通道：服务端推送实体变化，这里让对应查询失效，界面自动刷新，不需要轮询。
 */
export function RealtimeProvider({ children }: { children: React.ReactNode }) {
  const { token } = useSession();
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!token) {
      return;
    }

    // 经营分析的查询较重，业务数据连续变化（例如智能体批量执行）时同一查询最多每 10 秒刷新一次：
    // 距上次刷新已超过间隔就立即刷新，否则在间隔结束时补一次。
    const lastRefreshed = new Map<string, number>();
    const timers = new Map<string, ReturnType<typeof setTimeout>>();
    const invalidate = (key: readonly unknown[]) => {
      const interval = THROTTLED[String(key[0])];
      if (!interval) {
        void queryClient.invalidateQueries({ queryKey: key });
        return;
      }

      const id = JSON.stringify(key);
      const refresh = () => {
        timers.delete(id);
        lastRefreshed.set(id, Date.now());
        void queryClient.invalidateQueries({ queryKey: key });
      };
      const wait = (lastRefreshed.get(id) ?? 0) + interval - Date.now();
      if (wait <= 0) {
        refresh();
      } else if (!timers.has(id)) {
        timers.set(id, setTimeout(refresh, wait));
      }
    };

    const realtime = getRealtime();
    const off = realtime.onChange((change) => {
      for (const key of invalidationMap[change.entity]?.(change.id) ?? []) {
        invalidate(key);
      }
    });
    // 断线期间错过的变化不会补发，重新连上后把当前页面的数据全部重取一次。
    const offResync = realtime.onResync(() => void queryClient.invalidateQueries());
    realtime.start().catch(() => {
      // 客户端会在后台按退避节奏重试；实时通道不可用时界面仍可手动刷新。
    });

    return () => {
      for (const timer of timers.values()) {
        clearTimeout(timer);
      }

      off();
      offResync();
      void realtime.stop();
    };
  }, [token, queryClient]);

  return children;
}
