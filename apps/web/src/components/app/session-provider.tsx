'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan, type AuthResponse, type SessionDto } from '@ainative/api-client';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { readToken, writeToken } from '@/lib/token';

interface SessionContextValue {
  token: string | null;
  session: SessionDto | undefined;
  loading: boolean;
  /** 会话加载失败（例如服务器连不上、接口地址不对）时的错误；有会话数据时为 null。 */
  error: unknown;
  /** 重新加载会话。 */
  retry: () => void;
  retrying: boolean;
  signIn: (auth: AuthResponse) => void;
  signOut: () => void;
  switchTenant: (tenantId: string) => Promise<void>;
  can: (feature: string) => boolean;
}

const SessionContext = createContext<SessionContextValue | null>(null);

function subscribe(callback: () => void) {
  window.addEventListener('ainative:token', callback);
  window.addEventListener('storage', callback);
  return () => {
    window.removeEventListener('ainative:token', callback);
    window.removeEventListener('storage', callback);
  };
}

export function SessionProvider({ children }: { children: React.ReactNode }) {
  const queryClient = useQueryClient();
  const token = useSyncExternalStore(subscribe, readToken, () => null);
  const [hydrated, setHydrated] = useState(false);
  useEffect(() => setHydrated(true), []);

  const {
    data: session,
    isPending,
    error,
    isFetching,
    refetch,
  } = useQuery({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => api.auth.session(signal),
    enabled: !!token,
    staleTime: 60_000,
  });

  // 动作目录按当前身份的权限过滤，而且不会自动过期（见 lib/catalogs）：会话里的权限变化时（例如管理员调整了角色）一并刷新。
  const grants = session ? `${session.features.join(',')}|${session.disabledFeatures.join(',')}` : '';
  useEffect(() => {
    if (grants) {
      void queryClient.invalidateQueries({ queryKey: queryKeys.actions });
    }
  }, [grants, queryClient]);

  const signIn = useCallback(
    (auth: AuthResponse) => {
      queryClient.clear();
      queryClient.setQueryData(queryKeys.session, auth.session);
      writeToken(auth.accessToken);
    },
    [queryClient],
  );

  const signOut = useCallback(() => {
    writeToken(null);
    queryClient.clear();
  }, [queryClient]);

  const switchTenant = useCallback(
    async (tenantId: string) => {
      signIn(await api.auth.switchTenant({ tenantId }));
    },
    [signIn],
  );

  // 只在没有会话时关心重试进度，避免后台刷新会话时整个应用跟着重新渲染。
  const retrying = !session && isFetching;
  const value = useMemo<SessionContextValue>(
    () => ({
      token,
      session,
      loading: !hydrated || (!!token && isPending),
      error: session ? null : error,
      retry: () => void refetch(),
      retrying,
      signIn,
      signOut,
      switchTenant,
      can: (feature) => sessionCan(session, feature),
    }),
    [token, session, isPending, error, refetch, retrying, hydrated, signIn, signOut, switchTenant],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession() {
  const context = useContext(SessionContext);
  if (!context) {
    throw new Error('useSession must be used within SessionProvider');
  }

  return context;
}
