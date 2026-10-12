'use client';

import { Suspense } from 'react';
import { usePathname, useRouter, useSearchParams } from 'next/navigation';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { ApiKeysTab } from '@/features/settings/api-keys-tab';
import { MembersTab } from '@/features/settings/members-tab';
import { ModulesTab } from '@/features/settings/modules-tab';
import { RolesTab } from '@/features/settings/roles-tab';
import { TenantTab } from '@/features/settings/tenant-tab';
import { UsageTab } from '@/features/settings/usage-tab';

export default function SettingsPage() {
  return (
    <Suspense>
      <Settings />
    </Suspense>
  );
}

function Settings() {
  const { session, can } = useSession();
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const tabs = [
    { value: 'tenant', label: '空间', content: <TenantTab /> },
    { value: 'members', label: '成员', content: <MembersTab /> },
    { value: 'roles', label: '角色与权限', content: <RolesTab /> },
    { value: 'modules', label: '模块', content: <ModulesTab /> },
    ...(can('platform.usage.view') || can('agents.view') ? [{ value: 'usage', label: '用量与模型', content: <UsageTab /> }] : []),
    ...(can('platform.api_keys.use') ? [{ value: 'api-keys', label: 'API 密钥', content: <ApiKeysTab /> }] : []),
  ];
  // 当前标签记在地址里（?tab=），刷新、分享链接或从别处跳转时停在同一个标签；没有或无权查看时显示第一个。
  const requested = params.get('tab');
  const active = tabs.some((t) => t.value === requested) ? requested! : tabs[0].value;

  return (
    <div className='space-y-6 p-6'>
      <PageHeader title='设置' description={`管理空间「${session?.tenant.name ?? ''}」的名称与时区、成员、角色权限、模块、用量和 API 密钥。`} />
      <Tabs value={active} onValueChange={(tab) => router.replace(`${pathname}?tab=${tab}`, { scroll: false })}>
        {/* 标签较多，窄屏上横向滚动而不是挤压或换行。 */}
        <TabsList className='max-w-full justify-start overflow-x-auto'>
          {tabs.map((t) => (
            <TabsTrigger key={t.value} value={t.value} className='flex-none'>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
        {tabs.map((t) => (
          <TabsContent key={t.value} value={t.value} className='pt-4'>
            {t.content}
          </TabsContent>
        ))}
      </Tabs>
    </div>
  );
}
