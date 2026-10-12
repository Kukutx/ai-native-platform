import type { ReactNode } from 'react';
import { Redirect } from 'expo-router';
import { sessionCan } from '@ainative/api-client';
import { firstTabHref, tabFeatures, useSession, type GatedTab } from '@/lib/session';

/**
 * 没有权限的标签不渲染内容，直接跳到第一个有权限的标签。标签栏里这些标签已经隐藏，
 * 这里处理的是打开应用时默认落在第一个标签（对话）、或者权限在使用中被收回的情况。
 */
export function TabGuard({ tab, children }: { tab: GatedTab; children: ReactNode }) {
  const session = useSession();
  // 标签布局在会话加载完成后才渲染，这里只是兜底。
  if (!session.data) {
    return null;
  }

  if (!sessionCan(session.data, tabFeatures[tab])) {
    return <Redirect href={firstTabHref(session.data)} />;
  }

  return children;
}
