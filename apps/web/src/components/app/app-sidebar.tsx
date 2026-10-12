'use client';

import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { useTheme } from 'next-themes';
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
} from '@/components/ui/sidebar';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { useSession } from './session-provider';

const navigation = [
  { href: '/chat', label: '对话', icon: Icons.chat, feature: 'chat.use' },
  { href: '/agents', label: '智能体', icon: Icons.teams, feature: 'agents.view' },
  { href: '/tasks', label: '任务', icon: Icons.checks, feature: 'tasks.view' },
  { href: '/projects', label: '项目', icon: Icons.project, feature: 'projects.view' },
  { href: '/crm', label: '客户', icon: Icons.crm, feature: 'crm.view' },
  { href: '/erp', label: '进销存', icon: Icons.product, feature: 'erp.view' },
  { href: '/knowledge', label: '知识库', icon: Icons.knowledge, feature: 'knowledge.view' },
  { href: '/workflows', label: '自动化', icon: Icons.workflow, feature: 'workflows.view' },
  { href: '/runs', label: '执行记录', icon: Icons.clock, feature: 'runs.view' },
  { href: '/approvals', label: '审批', icon: Icons.badgeCheck, feature: 'approvals.view' },
  { href: '/settings', label: '设置', icon: Icons.settings, feature: 'platform.members.view' },
];

export function AppSidebar() {
  const pathname = usePathname();
  const { session, can, signOut, switchTenant } = useSession();
  const { resolvedTheme, setTheme } = useTheme();
  const pending = useQuery({
    queryKey: queryKeys.approvals('pending'),
    queryFn: ({ signal }) => api.approvals.list('pending', signal),
    enabled: can('approvals.view'),
  });

  return (
    <Sidebar collapsible='icon'>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <DropdownMenu>
              <DropdownMenuTrigger asChild>
                <SidebarMenuButton size='lg'>
                  <div className='bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-lg'>
                    <Icons.sparkles className='size-4' />
                  </div>
                  <div className='grid flex-1 text-left text-sm leading-tight'>
                    <span className='truncate font-medium'>{session?.tenant.name ?? '工作空间'}</span>
                    <span className='text-muted-foreground truncate text-xs'>AI 原生工作台</span>
                  </div>
                  <Icons.chevronsUpDown className='ml-auto size-4' />
                </SidebarMenuButton>
              </DropdownMenuTrigger>
              <DropdownMenuContent align='start' className='min-w-56'>
                <DropdownMenuLabel className='text-muted-foreground text-xs'>切换空间</DropdownMenuLabel>
                {session?.tenants.map((tenant) => (
                  <DropdownMenuItem key={tenant.id} onSelect={() => void switchTenant(tenant.id)}>
                    {tenant.name}
                    {tenant.id === session.tenant.id ? <Icons.check className='ml-auto size-4' /> : null}
                  </DropdownMenuItem>
                ))}
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
                    <SidebarMenuButton asChild isActive={pathname.startsWith(item.href)} tooltip={item.label}>
                      <Link href={item.href}>
                        <item.icon />
                        <span>{item.label}</span>
                      </Link>
                    </SidebarMenuButton>
                    {item.href === '/approvals' && pending.data?.length ? (
                      <SidebarMenuBadge>{pending.data.length}</SidebarMenuBadge>
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
                <SidebarMenuButton size='lg'>
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
