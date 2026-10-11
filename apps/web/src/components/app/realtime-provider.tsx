'use client';

import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { getRealtime } from '@/lib/api';
import { invalidationMap } from '@/lib/query-keys';
import { useSession } from './session-provider';

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

    const realtime = getRealtime();
    const off = realtime.onChange((change) => {
      for (const key of invalidationMap[change.entity]?.(change.id) ?? []) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
    });
    realtime.start().catch(() => {
      // 自动重连会继续尝试；实时通道不可用时界面仍可手动刷新。
    });

    return () => {
      off();
      void realtime.stop();
    };
  }, [token, queryClient]);

  return children;
}
