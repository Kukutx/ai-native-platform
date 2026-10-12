import type { Metadata } from 'next';
import Link from 'next/link';
import { Button } from '@/components/ui/button';
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyTitle } from '@/components/ui/empty';

export const metadata: Metadata = { title: '页面不存在' };

export default function NotFound() {
  return (
    <main className='flex min-h-svh items-center justify-center p-6'>
      <Empty className='max-w-md'>
        <EmptyHeader>
          <EmptyTitle>页面不存在</EmptyTitle>
          <EmptyDescription>链接可能已失效，或者页面已经移动。</EmptyDescription>
        </EmptyHeader>
        <EmptyContent>
          <Button asChild>
            <Link href='/'>回到工作台</Link>
          </Button>
        </EmptyContent>
      </Empty>
    </main>
  );
}
