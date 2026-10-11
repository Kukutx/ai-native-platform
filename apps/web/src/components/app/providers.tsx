'use client';

import { useState } from 'react';
import { MutationCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ThemeProvider } from 'next-themes';
import { toast } from 'sonner';
import { toProblem } from '@ainative/api-client';
import { Toaster } from '@/components/ui/sonner';
import { TooltipProvider } from '@/components/ui/tooltip';
import { RealtimeProvider } from './realtime-provider';
import { SessionProvider } from './session-provider';

export function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: { staleTime: 15_000, retry: (count, error) => toProblem(error).status >= 500 && count < 2 },
        },
        // 所有写操作的错误统一提示（后端返回中文 title）。
        mutationCache: new MutationCache({
          onError: (error) => {
            toast.error(toProblem(error).title);
          },
        }),
      }),
  );

  return (
    <ThemeProvider attribute='class' defaultTheme='system' enableSystem disableTransitionOnChange>
      <QueryClientProvider client={queryClient}>
        <SessionProvider>
          <RealtimeProvider>
            <TooltipProvider delayDuration={200}>{children}</TooltipProvider>
          </RealtimeProvider>
        </SessionProvider>
        <Toaster position='top-center' richColors />
      </QueryClientProvider>
    </ThemeProvider>
  );
}
