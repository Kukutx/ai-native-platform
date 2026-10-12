'use client';

import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useTheme } from 'next-themes';
import { toast } from 'sonner';
import type { TenantSummaryDto } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  useSidebar,
} from '@/components/ui/sidebar';
import { Spinner } from '@/components/ui/spinner';
import { api } from '@/lib/api';
import { PRODUCT_NAME } from '@/lib/config';
import { queryKeys } from '@/lib/query-keys';
import { navigation, navigationEntryFor } from './navigation';
import { useSession } from './session-provider';

export function AppSidebar() {
  const pathname = usePathname();
  const router = useRouter();
  const { session, can, signOut, switchTenant } = useSession();
  const { isMobile, setOpenMobile } = useSidebar();
  const { resolvedTheme, setTheme } = useTheme();
  const active = navigationEntryFor(pathname);
  const pending = useQuery({
    queryKey: queryKeys.approvals('pending'),
    queryFn: ({ signal }) => api.approvals.list('pending', signal),
    enabled: can('approvals.view'),
  });
  // 切换失败时由全局错误提示说明原因；成功后回到当前模块的首页，原来打开的记录属于上一个空间。
  const switching = useMutation({
    mutationFn: (tenant: TenantSummaryDto) => switchTenant(tenant.id),
    onSuccess: (_, tenant) => {
      toast.success(`已切换到「${tenant.name}」`);
      router.push(active?.href ?? '/');
    },
  });
  const closeOnMobile = () => {
    if (isMobile) {
      setOpenMobile(false);
    }
  };

  return (
    <Sidebar collapsible='icon'>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <SidebarMenuButton size='lg' disabled={switching.isPending} aria-label='切换空间'>
                  <div className='bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-lg'>
                    {switching.isPending ? <Spinner /> : <Icons.sparkles className='size-4' />}
                  </div>
                  <div className='grid flex-1 text-left text-sm leading-tight'>
                    <span className='truncate font-medium'>{switching.isPending ? '正在切换…' : (session?.tenant.name ?? '工作空间')}</span>
                    <span className='text-muted-foreground truncate text-xs'>{PRODUCT_NAME}</span>
                  </div>
                  <Icons.chevronsUpDown className='ml-auto size-4' />
                </SidebarMenuButton>
              </DropdownMenuTrigger>
              <DropdownMenuContent align='start' className='min-w-56'>
                <DropdownMenuLabel className='text-muted-foreground text-xs'>切换空间</DropdownMenuLabel>
                {session?.tenants.map((tenant) => {
                  const current = tenant.id === session.tenant.id;
                  return (
                    <DropdownMenuItem key={tenant.id} disabled={switching.isPending} onSelect={() => !current && switching.mutate(tenant)}>
                      {tenant.name}
                      {current ? <Icons.check className='ml-auto size-4' /> : null}
                    </DropdownMenuItem>
                  );
                })}
              </DropdownMenuContent>
            </DropdownMenu>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupContent>
            <SidebarMenu>
              {navigation
                .filter((item) => can(item.feature))
                .map((item) => (
                  <SidebarMenuItem key={item.href}>
                    <SidebarMenuButton asChild isActive={active?.href === item.href} tooltip={item.label}>
                      <Link href={item.href} onClick={closeOnMobile}>
                        <item.icon />
                        <span>{item.label}</span>
                      </Link>
                    </SidebarMenuButton>
                    {item.href === '/approvals' && pending.data?.length ? (
                      <SidebarMenuBadge aria-label={`${pending.data.length} 个待审批`}>{pending.data.length}</SidebarMenuBadge>
                    ) : null}
                  </SidebarMenuItem>
                ))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>
      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <SidebarMenuButton size='lg' aria-label='账号菜单'>
                  <div className='bg-muted flex size-8 items-center justify-center rounded-full text-sm font-medium'>
                    {session?.user.displayName.slice(0, 1) ?? '我'}
                  </div>
                  <div className='grid flex-1 text-left text-sm leading-tight'>
                    <span className='truncate font-medium'>{session?.user.displayName}</span>
                    <span className='text-muted-foreground truncate text-xs'>{session?.user.email}</span>
                  </div>
                </SidebarMenuButton>
              </DropdownMenuTrigger>
              <DropdownMenuContent align='end' side='top' className='min-w-56'>
                <DropdownMenuItem onSelect={() => setTheme(resolvedTheme === 'dark' ? 'light' : 'dark')}>
                  {resolvedTheme === 'dark' ? <Icons.sun className='size-4' /> : <Icons.moon className='size-4' />}
                  {resolvedTheme === 'dark' ? '浅色模式' : '深色模式'}
                </DropdownMenuItem>
                <DropdownMenuSeparator />
                <DropdownMenuItem onSelect={signOut}>
                  <Icons.logout className='size-4' />
                  退出登录
                </DropdownMenuItem>
              </DropdownMenuContent>
            </DropdownMenu>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
    </Sidebar>
  );
}
