'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { cn } from '@/lib/utils';

export interface SectionTab {
  href: string;
  label: string;
  match: (path: string) => boolean;
}

/** 一个业务模块内各页面顶部的切换条（客户管理、进销存等）。 */
export function SectionTabs({ tabs }: { tabs: SectionTab[] }) {
  // 静态导出后路径可能带结尾斜杠。
  const pathname = usePathname().replace(/\/+$/, '') || '/';

  return (
    <nav className='bg-muted text-muted-foreground inline-flex h-9 w-fit max-w-full items-center overflow-x-auto rounded-lg p-[3px] text-sm'>
      {tabs.map((tab) => (
        <Link
          key={tab.href}
          href={tab.href}
          className={cn(
            'shrink-0 rounded-md px-3 py-1 font-medium transition-colors',
            tab.match(pathname) ? 'bg-background text-foreground shadow-sm' : 'hover:text-foreground',
          )}
        >
          {tab.label}
        </Link>
      ))}
    </nav>
  );
}
