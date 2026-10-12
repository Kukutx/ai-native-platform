'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  const { token, loading } = useSession();
  const router = useRouter();

  useEffect(() => {
    if (!loading && token) {
      router.replace('/chat');
    }
  }, [loading, token, router]);

  return (
    <main className='bg-muted/40 flex min-h-svh flex-col items-center justify-center gap-6 p-6'>
      <div className='flex items-center gap-2 text-lg font-semibold'>
        <Icons.sparkles className='size-5' />
        AI 原生工作台
      </div>
      <div className='w-full max-w-sm'>{children}</div>
    </main>
  );
}
