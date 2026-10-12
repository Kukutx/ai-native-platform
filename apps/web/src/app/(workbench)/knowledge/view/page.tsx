'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useConfirm } from '@/components/app/confirm-provider';
import { Markdown } from '@/components/app/markdown';
import { DetailState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { DocumentFormDialog, type DocumentDraft } from '@/features/knowledge/document-form-dialog';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function DocumentDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const [draft, setDraft] = useState<DocumentDraft | null>(null);
  const detail = useQuery({ queryKey: queryKeys.knowledgeDocument(id), queryFn: ({ signal }) => api.knowledge.getDocument(id, signal), enabled: !!id });
  const remove = useMutation({
    mutationFn: () => api.knowledge.deleteDocument(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['knowledge'] });
      router.push('/knowledge');
    },
  });

  if (!detail.data) {
    return <DetailState id={id} query={detail} subject='文档' back={{ href: '/knowledge', label: '知识库' }} />;
  }

  const { document, content, chunks } = detail.data;
  return (
    <div className='space-y-6 p-6'>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <Link href='/knowledge' className='text-muted-foreground inline-flex items-center gap-1 text-sm hover:underline'>
            <Icons.chevronLeft className='size-4' />
            知识库
          </Link>
          <h1 className='text-2xl font-semibold tracking-tight'>{document.title}</h1>
          <div className='text-muted-foreground flex flex-wrap items-center gap-2 text-sm'>
            {document.tags.map((t) => (
              <Badge key={t} variant='secondary'>
                {t}
              </Badge>
            ))}
            <span>
              {document.createdBy.name} 创建 · 更新于 {formatTime(document.updatedAt)} · {document.contentLength.toLocaleString()} 字 · {document.chunkCount} 个片段
            </span>
          </div>
        </div>
        {can('knowledge.manage') ? (
          <div className='flex gap-2'>
            <Button
              variant='outline'
              onClick={() => setDraft({ id: document.id, title: document.title, content, fileName: document.fileName, tags: document.tags })}
            >
              <Icons.edit />
              编辑
            </Button>
            <Button
              variant='ghost'
              aria-label='删除文档'
              disabled={remove.isPending}
              onClick={() => void confirm({ title: `删除《${document.title}》？`, confirmLabel: '删除' }).then((ok) => ok && remove.mutate())}
            >
              <Icons.trash />
            </Button>
          </div>
        ) : null}
      </div>
      <Tabs defaultValue='content'>
        <TabsList>
          <TabsTrigger value='content'>正文</TabsTrigger>
          <TabsTrigger value='chunks'>检索片段</TabsTrigger>
        </TabsList>
        <TabsContent value='content' className='pt-4'>
          <Card>
            <CardContent className='pt-6'>
              <Markdown className='leading-7'>{content}</Markdown>
            </CardContent>
          </Card>
        </TabsContent>
        <TabsContent value='chunks' className='space-y-3 pt-4'>
          <p className='text-muted-foreground text-sm'>文档按段落和标题切成片段分别检索，每个片段带上所在章节的标题路径。</p>
          {chunks.map((chunk) => (
            <div key={chunk.id} className='bg-card rounded-xl border p-3'>
              <div className='text-muted-foreground mb-1 flex items-center justify-between text-xs'>
                片段 {chunk.seq + 1}
                <Badge variant={chunk.embedded ? 'success' : 'outline'}>{chunk.embedded ? '已向量化' : '向量化中'}</Badge>
              </div>
              <p className='text-sm whitespace-pre-wrap'>{chunk.content}</p>
            </div>
          ))}
        </TabsContent>
      </Tabs>
      <DocumentFormDialog draft={draft} onClose={() => setDraft(null)} />
    </div>
  );
}

export default function DocumentPage() {
  return (
    <Suspense>
      <DocumentDetail />
    </Suspense>
  );
}
