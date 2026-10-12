'use client';

import { PageHeader } from '@/components/app/page-header';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { McpServersTab } from '@/features/integrations/mcp-servers-tab';
import { SecretsTab } from '@/features/integrations/secrets-tab';

export default function IntegrationsPage() {
  return (
    <div className='space-y-6 p-6'>
      <PageHeader title='集成' description='接入外部 MCP 服务，让智能体、对话和工作流调用它们的工具；外部服务的令牌加密保存在密钥里。' />
      <Tabs defaultValue='mcp'>
        <TabsList>
          <TabsTrigger value='mcp'>外部服务</TabsTrigger>
          <TabsTrigger value='secrets'>密钥</TabsTrigger>
        </TabsList>
        <TabsContent value='mcp' className='pt-4'>
          <McpServersTab />
        </TabsContent>
        <TabsContent value='secrets' className='pt-4'>
          <SecretsTab />
        </TabsContent>
      </Tabs>
    </div>
  );
}
