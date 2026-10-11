'use client';

import { createContext, useCallback, useContext, useEffect, useMemo, useState, useSyncExternalStore } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { AuthResponse, SessionDto } from '@ainative/api-client';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { readToken, writeToken } from '@/lib/token';

interface SessionContextValue {
  token: string | null;
  session: SessionDto | undefined;
  loading: boolean;
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

/** 与后端 FeatureMatcher 相同的规则：精确、模块通配（tasks.*）、全部（*）。 */
export function isGranted(features: readonly string[], feature: string) {
  return features.some((grant) => grant === '*' || grant === feature || (grant.endsWith('.*') && feature.startsWith(grant.slice(0, -1))));
}

export function SessionProvider({ children }: { children: React.ReactNode }) {
  const queryClient = useQueryClient();
  const token = useSyncExternalStore(subscribe, readToken, () => null);
  const [hydrated, setHydrated] = useState(false);
  useEffect(() => setHydrated(true), []);

  const sessionQuery = useQuery({
    queryKey: queryKeys.session,
    queryFn: ({ signal }) => api.auth.session(signal),
    enabled: !!token,
    staleTime: 60_000,
  });

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

  const value = useMemo<SessionContextValue>(() => {
    const features = sessionQuery.data?.features ?? [];
    // 角色授予了、但所属模块在当前空间停用的功能，后端同样会拒绝，界面上也不显示入口。
    const disabled = new Set(sessionQuery.data?.disabledFeatures ?? []);
    return {
      token,
      session: sessionQuery.data,
      loading: !hydrated || (!!token && sessionQuery.isPending),
      signIn,
      signOut,
      switchTenant,
      can: (feature) => !disabled.has(feature) && isGranted(features, feature),
    };
  }, [token, sessionQuery.data, sessionQuery.isPending, hydrated, signIn, signOut, switchTenant]);

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession() {
  const context = useContext(SessionContext);
  if (!context) {
    throw new Error('useSession must be used within SessionProvider');
  }

  return context;
}
