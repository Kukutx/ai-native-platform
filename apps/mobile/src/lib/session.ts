import { useQuery } from '@tanstack/react-query';
import { sessionCan, type SessionDto } from '@ainative/api-client';
import { getApi } from './api';

/** 当前会话：用户、空间、角色授予的功能和停用的模块。 */
export function useSession() {
  return useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
}

/** 返回判断函数：当前会话能否使用某项功能。会话还没加载时一律为 false。 */
export function useCan() {
  const { data } = useSession();
  return (feature: string) => sessionCan(data, feature);
}

/** 需要权限的底部标签（顺序与标签栏一致）；“我的”不需要权限，总是可用。 */
export const tabFeatures = {
  index: 'chat.use',
  tasks: 'tasks.view',
  crm: 'crm.view',
  knowledge: 'knowledge.view',
  approvals: 'approvals.view',
} as const;

export type GatedTab = keyof typeof tabFeatures;

const tabHrefs = { index: '/', tasks: '/tasks', crm: '/crm', knowledge: '/knowledge', approvals: '/approvals' } as const;

/** 第一个有权限的标签。例如只读角色没有对话权限，打开应用时落在任务页而不是一个打不开的对话列表。 */
export function firstTabHref(session: SessionDto | undefined) {
  const tab = (Object.keys(tabFeatures) as GatedTab[]).find((t) => sessionCan(session, tabFeatures[t]));
  return tab ? tabHrefs[tab] : '/me';
}
