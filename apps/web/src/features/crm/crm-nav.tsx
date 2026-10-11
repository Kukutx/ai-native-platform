'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { cn } from '@/lib/utils';

const tabs = [
  { href: '/crm', label: '销售看板', match: (path: string) => path === '/crm' || path.startsWith('/crm/deals') },
  { href: '/crm/companies', label: '客户', match: (path: string) => path.startsWith('/crm/companies') },
  { href: '/crm/contacts', label: '联系人', match: (path: string) => path.startsWith('/crm/contacts') },
];

/** 客户管理各页面顶部的切换条。 */
export function CrmNav() {
  // 静态导出后路径可能带结尾斜杠。
  const pathname = usePathname().replace(/\/+$/, '') || '/';

  return (
    <nav className='bg-muted text-muted-foreground inline-flex h-9 w-fit items-center rounded-lg p-[3px] text-sm'>
      {tabs.map((tab) => (
        <Link
          key={tab.href}
          href={tab.href}
          className={cn(
            'rounded-md px-3 py-1 font-medium transition-colors',
            tab.match(pathname) ? 'bg-background text-foreground shadow-sm' : 'hover:text-foreground',
          )}
        >
          {tab.label}
        </Link>
      ))}
    </nav>
  );
}
