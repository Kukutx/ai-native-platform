'use client';

import { useState } from 'react';
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from 'next-themes';
import { toast } from 'sonner';
import { toProblem } from '@ainative/api-client';
import { Toaster } from '@/components/ui/sonner';
import { TooltipProvider } from '@/components/ui/tooltip';
import { errorMessage, mutationErrorMessage } from '@/lib/errors';
import { ConfirmProvider } from './confirm-provider';
import { RealtimeProvider } from './realtime-provider';
import { SessionProvider } from './session-provider';

export function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { staleTime: 15_000, retry: (count, error) => toProblem(error).status >= 500 && count < 2 },
        },
        // 已经有数据的查询在后台刷新失败时提示一次（界面继续显示旧数据）；首次加载失败由页面上的错误状态展示。
        // 登录失效（401）会直接回到登录页，不再提示。
        queryCache: new QueryCache({
          onError: (error, query) => {
            if (query.state.data !== undefined && toProblem(error).status !== 401) {
              toast.error(`刷新失败：${errorMessage(error)}`, { id: 'background-refetch-error' });
            }
          },
        }),
        // 所有写操作的错误统一提示（后端返回中文 title）；参数校验失败时直接列出具体原因。
        // 界面自己处理的错误（meta.handledError）不再重复提示；声明了 meta.successMessage 的写操作成功后统一提示。
        mutationCache: new MutationCache({
          onError: (error, _variables, _onMutateResult, mutation) => {
            if (mutation.meta?.handledError?.(error)) {
              return;
            }

            toast.error(mutationErrorMessage(error));
          },
          onSuccess: (_data, _variables, _onMutateResult, mutation) => {
            if (mutation.meta?.successMessage) {
              toast.success(mutation.meta.successMessage);
            }
          },
        }),
      }),
  );

  return (
    <ThemeProvider attribute='class' defaultTheme='system' enableSystem disableTransitionOnChange>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <RealtimeProvider>
            <TooltipProvider delayDuration={200}>
              <ConfirmProvider>{children}</ConfirmProvider>
            </TooltipProvider>
          </RealtimeProvider>
        </SessionProvider>
        <Toaster position='top-center' richColors />
      </QueryClientProvider>
    </ThemeProvider>
  );
}
