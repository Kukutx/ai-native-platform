'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { AppSidebar } from '@/components/app/app-sidebar';
import { useSession } from '@/components/app/session-provider';
import { SidebarInset, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar';
import { Spinner } from '@/components/ui/spinner';

export default function WorkbenchLayout({ children }: { children: React.ReactNode }) {
  const { token, session, loading } = useSession();
  const router = useRouter();

  useEffect(() => {
    if (!loading && !token) {
      router.replace('/login');
    }
  }, [loading, token, router]);

  if (loading || !token || !session) {
    return (
      <div className='flex min-h-svh items-center justify-center'>
        <Spinner />
      </div>
    );
  }

  return (
    <SidebarProvider>
      <AppSidebar />
      <SidebarInset className='min-w-0'>
        <header className='flex h-12 shrink-0 items-center gap-2 border-b px-4 md:hidden'>
          <SidebarTrigger />
        </header>
        <div className='flex min-h-0 flex-1 flex-col'>{children}</div>
      </SidebarInset>
    </SidebarProvider>
  );
}
