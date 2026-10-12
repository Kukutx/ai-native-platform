'use client';

import { useDeferredValue, useRef, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { DocumentFormDialog, emptyDraft, type DocumentDraft } from './document-form-dialog';

const ACCEPTED = '.md,.markdown,.txt,.csv,.json,.html';
const MAX_UPLOAD_BYTES = 2 * 1024 * 1024;

export function DocumentsTab() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const manage = can('knowledge.manage');
  const fileInput = useRef<HTMLInputElement>(null);
  const [filter, setFilter] = useState('');
  const term = useDeferredValue(filter.trim());
  const [draft, setDraft] = useState<DocumentDraft | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);
  const documents = useQuery({
    queryKey: queryKeys.knowledgeDocuments(term),
    queryFn: ({ signal }) => api.knowledge.listDocuments(term || undefined, signal),
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.knowledge.deleteDocument(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['knowledge'] }),
  });

  async function upload(file: File) {
    setUploadError(null);
    if (file.size > MAX_UPLOAD_BYTES) {
      setUploadError('文件超过 2 MB，请拆分后再上传。');
      return;
    }

    const content = await file.text();
    setDraft({ title: file.name.replace(/\.[^.]+$/, ''), content, fileName: file.name, tags: [] });
  }

  return (
    <div className='space-y-4'>
      <div className='flex flex-wrap items-center justify-between gap-2'>
        <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder='按标题或标签筛选' className='w-56' />
        {manage ? (
          <div className='flex gap-2'>
            <input
              ref={fileInput}
              type='file'
              accept={ACCEPTED}
              className='hidden'
              onChange={(e) => {
                const file = e.target.files?.[0];
                e.target.value = '';
                if (file) {
                  void upload(file);
                }
              }}
            />
            <Button variant='outline' onClick={() => fileInput.current?.click()}>
              <Icons.upload />
              上传文件
            </Button>
            <Button onClick={() => setDraft(emptyDraft)}>
              <Icons.add />
              新建文档
            </Button>
          </div>
        ) : null}
      </div>
      {uploadError ? <p className='text-destructive text-sm'>{uploadError}</p> : null}
      {documents.isPending ? (
        <Skeleton className='h-40 w-full' />
      ) : documents.data?.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>标题</TableHead>
              <TableHead>标签</TableHead>
              <TableHead>片段</TableHead>
              <TableHead>创建人</TableHead>
              <TableHead>更新时间</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {documents.data.map((d) => (
              <TableRow key={d.id}>
                <TableCell className='max-w-80'>
                  <Link href={`/knowledge/view?id=${d.id}`} className='font-medium hover:underline'>
                    {d.title}
                  </Link>
                  {d.fileName ? <div className='text-muted-foreground truncate text-xs'>{d.fileName}</div> : null}
                </TableCell>
                <TableCell className='space-x-1'>
                  {d.tags.map((t) => (
                    <Badge key={t} variant='secondary'>
                      {t}
                    </Badge>
                  ))}
                </TableCell>
                <TableCell className='text-muted-foreground text-sm'>
                  {d.chunkCount}
                  {d.embeddedChunkCount < d.chunkCount ? <span className='ml-1 text-xs'>（向量化中 {d.embeddedChunkCount}/{d.chunkCount}）</span> : null}
                </TableCell>
                <TableCell className='text-sm'>{d.createdBy.name}</TableCell>
                <TableCell className='text-muted-foreground text-sm'>{formatTime(d.updatedAt)}</TableCell>
                <TableCell className='text-right'>
                  {manage ? (
                    <Button size='sm' variant='ghost' onClick={() => confirm(`删除《${d.title}》？`) && remove.mutate(d.id)}>
                      <Icons.trash />
                    </Button>
                  ) : null}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <EmptyState
          title={term ? '没有匹配的文档' : '知识库还是空的'}
          description='把制度、流程、产品资料放进来，智能体和对话会在需要时自动检索。'
        />
      )}
      <DocumentFormDialog draft={draft} onClose={() => setDraft(null)} />
    </div>
  );
}
