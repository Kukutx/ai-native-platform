'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { PRODUCT_NAME } from '@/lib/config';

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  const { token, loading } = useSession();
  const router = useRouter();

  // 已登录时交给首页，按权限进入第一个可用的入口。
  useEffect(() => {
    if (!loading && token) {
      router.replace('/');
    }
  }, [loading, token, router]);

  return (
    <main className='bg-muted/40 flex min-h-svh flex-col items-center justify-center gap-6 p-6'>
      <div className='flex items-center gap-2 text-lg font-semibold'>
        <Icons.sparkles className='size-5' />
        {PRODUCT_NAME}
      </div>
      <div className='w-full max-w-sm'>{children}</div>
    </main>
  );
}
