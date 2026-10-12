import { useQuery } from '@tanstack/react-query';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/** 站点设置（是否开放注册等），登录前就能读取。 */
export function useAuthOptions() {
  return useQuery({ queryKey: queryKeys.authOptions, queryFn: ({ signal }) => api.auth.options(signal), staleTime: 5 * 60_000 });
}
