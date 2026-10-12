'use client';

import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { EmptyState } from '@/components/app/empty-state';
import { firstAllowedEntry } from '@/components/app/navigation';
import { useSession } from '@/components/app/session-provider';

/** 首页（也是登录后的落地页）：进入当前身份可以使用的第一个入口。 */
export default function HomePage() {
  const router = useRouter();
  const { can } = useSession();
  const target = firstAllowedEntry(can)?.href;

  useEffect(() => {
    if (target) {
      router.replace(target);
    }
  }, [router, target]);

  if (target) {
    return null;
  }

  return (
    <div className='p-6'>
      <EmptyState title='暂时没有可以使用的功能' description='你在这个空间的角色还没有被授予任何功能权限，请联系空间管理员分配角色，或者切换到其他空间。' />
    </div>
  );
}
