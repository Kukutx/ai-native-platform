import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { RealtimeClient, type RealtimeChange } from '@ainative/api-client/realtime';
import { auth, useAuth } from './auth';

let current: { baseUrl: string; client: RealtimeClient } | null = null;

function getRealtime() {
  const { baseUrl } = auth.get();
  if (current?.baseUrl !== baseUrl) {
    void current?.client.stop();
    current = { baseUrl, client: new RealtimeClient({ baseUrl, getToken: () => auth.get().token }) };
  }

  return current.client;
}

/** 实体变化 → 需要失效的查询前缀（与 Web 端 query-keys 的约定一致）。 */
const invalidation: Record<RealtimeChange['entity'], (id: string) => readonly (readonly unknown[])[]> = {
  agent: () => [['agents']],
  work_item: (id) => [['work-items'], ['work-item', id]],
  run: () => [['agents'], ['work-items']],
  approval: () => [['approvals']],
  conversation: () => [['conversations']],
  module: () => [['session']],
  knowledge_document: () => [['knowledge']],
  memory: () => [['knowledge', 'search']],
};

/** 登录后连接 SignalR 实时通道，服务端推送变化时让对应查询失效，界面自动刷新。 */
export function useRealtimeSync() {
  const { token, baseUrl } = useAuth();
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!token) {
      return;
    }

    const realtime = getRealtime();
    const off = realtime.onChange((change) => {
      for (const key of invalidation[change.entity]?.(change.id) ?? []) {
        void queryClient.invalidateQueries({ queryKey: key });
      }
    });
    // 断线期间错过的变化不会补发，重新连上后把当前页面的数据全部重取一次。
    const offResync = realtime.onResync(() => void queryClient.invalidateQueries());
    realtime.start().catch(() => {
      // 客户端会在后台按退避节奏重试；连不上时下拉刷新仍然可用。
    });

    return () => {
      off();
      offResync();
      void realtime.stop();
    };
  }, [token, baseUrl, queryClient]);
}
