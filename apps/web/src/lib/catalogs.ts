import { queryOptions } from '@tanstack/react-query';
import type { CrmRecordType } from '@ainative/api-client';
import { api } from './api';
import { queryKeys } from './query-keys';

/**
 * 目录类数据（模型、动作、模块、商机阶段、自定义字段、仓库）很少变化：
 * 变化时实时通道会让它们失效（断线重连后全部重取），所以不设过期时间，切换页面、窗口聚焦时不重复请求。
 * 模型目录来自服务端配置，只随部署变化。动作目录还取决于当前身份的权限，会话里的权限变化时由 SessionProvider 刷新。
 */
export const catalogQueries = {
  models: () => queryOptions({ queryKey: queryKeys.models, queryFn: ({ signal }) => api.models.catalog(signal), staleTime: Infinity }),
  actions: () => queryOptions({ queryKey: queryKeys.actions, queryFn: ({ signal }) => api.actions.list(signal), staleTime: Infinity }),
  modules: () => queryOptions({ queryKey: queryKeys.modules, queryFn: ({ signal }) => api.modules.list(signal), staleTime: Infinity }),
  crmStages: () => queryOptions({ queryKey: queryKeys.crmStages, queryFn: ({ signal }) => api.crm.stages.list(signal), staleTime: Infinity }),
  crmFields: (recordType?: CrmRecordType) =>
    queryOptions({ queryKey: queryKeys.crmFields(recordType), queryFn: ({ signal }) => api.crm.fields.list(recordType, signal), staleTime: Infinity }),
  erpWarehouses: () => queryOptions({ queryKey: queryKeys.erpWarehouses, queryFn: ({ signal }) => api.erp.warehouses.list(signal), staleTime: Infinity }),
};
