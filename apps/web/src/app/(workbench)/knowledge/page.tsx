'use client';

import { useDeferredValue, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Input } from '@/components/ui/input';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { DocumentsTab } from '@/features/knowledge/documents-tab';
import { MemoriesTab } from '@/features/knowledge/memories-tab';
import { SearchResults } from '@/features/knowledge/search-results';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

export default function KnowledgePage() {
  const { can } = useSession();
  const [search, setSearch] = useState('');
  const query = useDeferredValue(search.trim());
  const status = useQuery({ queryKey: queryKeys.knowledgeStatus, queryFn: ({ signal }) => api.knowledge.status(signal) });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='知识库'
        description='团队文档和长期记忆。对话、智能体和外部 MCP 客户端检索的是同一份知识。'
        actions={
          status.data ? (
            <span className='text-muted-foreground text-xs'>
              向量模型 {status.data.embeddingModel}
              {status.data.isDemoEmbedding ? '（离线演示，只看字面相似）' : null}
              {status.data.pendingEmbeddings > 0 ? ` · ${status.data.pendingEmbeddings} 个片段向量化中` : null}
            </span>
          ) : null
        }
      />
      <div className='relative max-w-2xl'>
        <Icons.search className='text-muted-foreground pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2' />
        <Input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder='用自然语言检索文档和记忆，例如：出差住宿每晚能报多少？'
          className='h-11 pl-9'
        />
      </div>
      {query ? (
        <SearchResults query={query} />
      ) : (
        <Tabs defaultValue='documents'>
          <TabsList>
            <TabsTrigger value='documents'>文档{status.data ? ` ${status.data.documents}` : ''}</TabsTrigger>
            {can('memory.use') ? <TabsTrigger value='memories'>记忆{status.data ? ` ${status.data.memories}` : ''}</TabsTrigger> : null}
          </TabsList>
          <TabsContent value='documents' className='pt-4'>
            <DocumentsTab />
          </TabsContent>
          {can('memory.use') ? (
            <TabsContent value='memories' className='pt-4'>
              <MemoriesTab />
            </TabsContent>
          ) : null}
        </Tabs>
      )}
    </div>
  );
}
