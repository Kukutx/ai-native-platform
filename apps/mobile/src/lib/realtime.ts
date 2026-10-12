import { useEffect } from 'react';
import { AppState } from 'react-native';
import { useQueryClient } from '@tanstack/react-query';
import { RealtimeClient } from '@ainative/api-client/realtime';
import { auth, useAuth } from './auth';

let current: { baseUrl: string; client: RealtimeClient } | null = null;

/** 当前服务器的实时通道（SignalR）；服务器地址变化时换一个新的客户端。 */
export function getRealtime() {
  const { baseUrl } = auth.get();
  if (current?.baseUrl !== baseUrl) {
    void current?.client.stop();
    current = { baseUrl, client: new RealtimeClient({ baseUrl, getToken: () => auth.get().token }) };
  }

  return current.client;
}

/**
 * 实体变化 → 需要失效的查询前缀（与 Web 端 query-keys 的约定一致）。
 * 按字符串索引：后端新增的实体类型在这里没有登记时直接忽略。
 */
const invalidation: Record<string, (id: string) => readonly (readonly unknown[])[]> = {
  agent: () => [['agents'], ['runs']],
  work_item: (id) => [['work-items'], ['work-item', id], ['projects'], ['project']],
  run: (id) => [['runs'], ['run', id], ['agents'], ['work-items']],
  approval: () => [['approvals']],
  conversation: () => [['conversations']],
  module: () => [['session']],
  knowledge_document: () => [['knowledge']],
  memory: () => [['knowledge', 'search']],
  workflow: () => [['workflows']],
  workflow_run: (id) => [['workflow-runs'], ['workflow-run', id], ['workflows']],
  crm_company: () => [['crm'], ['erp']],
  crm_contact: () => [['crm'], ['erp']],
  crm_deal: () => [['crm'], ['erp']],
  crm_activity: () => [['crm']],
  crm_stage: () => [['crm']],
  crm_field: () => [['crm']],
  erp_product: () => [['erp']],
  erp_warehouse: () => [['erp']],
  erp_stock: () => [['erp']],
  erp_sales_order: () => [['erp']],
  erp_purchase_order: () => [['erp']],
  erp_stock_document: () => [['erp']],
  project: (id) => [['projects'], ['project', id], ['work-items']],
  // 经营分析数据较重，手机端不随业务变化实时重算，靠下拉刷新和重新进入页面更新。
  analytics_report: () => [],
  tenant: () => [['session'], ['analytics']],
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
    const connect = () =>
      realtime.start().catch(() => {
        // 客户端会在后台按退避节奏重试；连不上时下拉刷新仍然可用。
      });
    connect();

    // 切到后台时主动断开（系统迟早会掐掉后台的长连接，留着只费电）；回到前台立即重连，
    // 不等退避计时器。重连成功会触发上面的 onResync，把离开期间错过的变化重取一遍。
    const appState = AppState.addEventListener('change', (status) => {
      if (status === 'active') {
        connect();
      } else if (status === 'background') {
        void realtime.stop();
      }
    });

    return () => {
      appState.remove();
      off();
      offResync();
      void realtime.stop();
    };
  }, [token, baseUrl, queryClient]);
}
