'use client';

import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { MembersTab } from '@/features/settings/members-tab';
import { ModulesTab } from '@/features/settings/modules-tab';
import { RolesTab } from '@/features/settings/roles-tab';
import { UsageTab } from '@/features/settings/usage-tab';

export default function SettingsPage() {
  const { session, can } = useSession();
  const showUsage = can('platform.usage.view') || can('agents.view');

  return (
    <div className='space-y-6 p-6'>
      <PageHeader title='设置' description={`管理空间「${session?.tenant.name ?? ''}」的成员、角色权限、模块和用量。`} />
      <Tabs defaultValue='members'>
        <TabsList>
          <TabsTrigger value='members'>成员</TabsTrigger>
          <TabsTrigger value='roles'>角色与权限</TabsTrigger>
          <TabsTrigger value='modules'>模块</TabsTrigger>
          {showUsage ? <TabsTrigger value='usage'>用量与模型</TabsTrigger> : null}
        </TabsList>
        <TabsContent value='members' className='pt-4'>
          <MembersTab />
        </TabsContent>
        <TabsContent value='roles' className='pt-4'>
          <RolesTab />
        </TabsContent>
        <TabsContent value='modules' className='pt-4'>
          <ModulesTab />
        </TabsContent>
        {showUsage ? (
          <TabsContent value='usage' className='pt-4'>
            <UsageTab />
          </TabsContent>
        ) : null}
      </Tabs>
    </div>
  );
}
