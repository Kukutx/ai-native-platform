'use client';

import { useState } from 'react';
import { MutationCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from 'next-themes';
import { toast } from 'sonner';
import { toProblem } from '@ainative/api-client';
import { Toaster } from '@/components/ui/sonner';
import { TooltipProvider } from '@/components/ui/tooltip';
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
        // 所有写操作的错误统一提示（后端返回中文 title）；参数校验失败时直接列出具体原因。
        mutationCache: new MutationCache({
          onError: (error) => {
            const problem = toProblem(error);
            const reasons = Object.values(problem.errors ?? {}).flat();
            toast.error(reasons.length ? reasons.slice(0, 3).join(' ') : problem.title);
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
