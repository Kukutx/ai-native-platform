'use client';

import { useEffect } from 'react';
import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { AppSidebar } from '@/components/app/app-sidebar';
import { firstAllowedEntry, navigationEntryFor } from '@/components/app/navigation';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card';
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty';
import { SidebarInset, SidebarProvider, SidebarTrigger } from '@/components/ui/sidebar';
import { Spinner } from '@/components/ui/spinner';
import { API_URL, absoluteApiUrl } from '@/lib/config';
import { errorMessage } from '@/lib/errors';

export default function WorkbenchLayout({ children }: { children: React.ReactNode }) {
  const { token, session, loading, error } = useSession();
  const router = useRouter();

  useEffect(() => {
    if (!loading && !token) {
      router.replace('/login');
    }
  }, [loading, token, router]);

  if (token && !session && error) {
    return <SessionError />;
  }

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
        <div className='flex min-h-0 flex-1 flex-col'>
          <RouteGuard>{children}</RouteGuard>
        </div>
      </SidebarInset>
    </SidebarProvider>
  );
}

/** 会话加载失败（服务器没启动、接口地址配置错误、网络断开等）：说明原因，可以重试或退出重新登录。 */
function SessionError() {
  const { error, retry, retrying, signOut } = useSession();

  return (
    <main className='bg-muted/40 flex min-h-svh items-center justify-center p-6'>
      <Card className='w-full max-w-md'>
        <CardHeader>
          <CardTitle className='flex items-center gap-2'>
            <Icons.warning className='text-destructive size-5' />
            无法连接工作台服务
          </CardTitle>
          <CardDescription>{errorMessage(error)}</CardDescription>
        </CardHeader>
        <CardContent className='space-y-2 text-sm'>
          <p className='text-muted-foreground'>当前连接的接口地址：</p>
          <p className='bg-muted rounded-md px-3 py-2 font-mono text-xs break-all'>{API_URL || `${absoluteApiUrl('')}（与页面同源）`}</p>
          <p className='text-muted-foreground text-xs'>请确认后端服务已经启动、地址可以访问；地址不对时需要重新构建或修改部署配置。</p>
        </CardContent>
        <CardFooter className='flex flex-wrap justify-end gap-2'>
          <Button variant='outline' onClick={signOut}>
            <Icons.logout />
            退出登录
          </Button>
          <Button disabled={retrying} onClick={retry}>
            {retrying ? <Spinner /> : <Icons.refresh />}
            重试
          </Button>
        </CardFooter>
      </Card>
    </main>
  );
}

/**
 * 页面级权限检查：一级入口需要的功能权限与侧栏一致，没有权限或模块未启用时不渲染页面，
 * 避免页面发出一串注定 403 的请求、显示成空数据。
 */
function RouteGuard({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { can } = useSession();
  const entry = navigationEntryFor(pathname);

  if (!entry || can(entry.feature)) {
    return children;
  }

  const fallback = firstAllowedEntry(can);
  return (
    <div className='p-6'>
      <Empty className='border border-dashed'>
        <EmptyHeader>
          <EmptyMedia variant='icon'>
            <Icons.lock />
          </EmptyMedia>
          <EmptyTitle>无权访问或模块未启用</EmptyTitle>
          <EmptyDescription>你的角色没有「{entry.label}」的使用权限，或者这个模块没有在当前空间启用。需要时请联系空间管理员。</EmptyDescription>
        </EmptyHeader>
        {fallback ? (
          <EmptyContent>
            <Button variant='outline' asChild>
              <Link href={fallback.href}>前往{fallback.label}</Link>
            </Button>
          </EmptyContent>
        ) : null}
      </Empty>
    </div>
  );
}
