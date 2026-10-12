'use client';

import { useDeferredValue, useRef, useState } from 'react';
import Link from 'next/link';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useConfirm } from '@/components/app/confirm-provider';
import { EmptyState } from '@/components/app/empty-state';
import { ErrorState } from '@/components/app/query-state';
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

// 与后端解析器支持的扩展名一致；老版 Office 格式（.doc 等）由后端提示另存为新格式。
const ACCEPTED = '.pdf,.docx,.xlsx,.pptx,.md,.markdown,.txt,.text,.csv,.tsv,.json,.log,.xml,.yaml,.yml,.html,.htm';
const MAX_UPLOAD_BYTES = 20 * 1024 * 1024;

/** 页数在不同格式里的含义。 */
const PAGE_UNITS: Record<string, string> = { PDF: '页', Excel: '个工作表', PowerPoint: '页幻灯片' };

export function DocumentsTab() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
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
  // 文件交给后端提取正文（PDF、Word、Excel、PowerPoint、网页、文本），确认后再保存成文档。
  const extract = useMutation({
    mutationFn: (file: File) => api.knowledge.extractFile({ data: file, fileName: file.name }),
    onSuccess: (result) => {
      const unit = result.pages ? PAGE_UNITS[result.format] : undefined;
      setDraft({
        title: result.title,
        content: result.content,
        fileName: result.fileName,
        tags: [],
        source: `已从 ${result.fileName} 提取正文（${result.format}${unit ? `，${result.pages} ${unit}` : ''}），保存前可以修改。`,
        warnings: result.warnings,
      });
    },
  });

  function upload(file: File) {
    setUploadError(null);
    if (file.size > MAX_UPLOAD_BYTES) {
      setUploadError('文件超过 20 MB，请拆分后再上传。');
      return;
    }

    extract.mutate(file);
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
                  upload(file);
                }
              }}
            />
            <Button variant='outline' disabled={extract.isPending} onClick={() => fileInput.current?.click()}>
              {extract.isPending ? <Icons.spinner className='animate-spin' /> : <Icons.upload />}
              {extract.isPending ? '正在读取文件…' : '上传文件'}
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
      ) : documents.isError && !documents.data ? (
        <ErrorState query={documents} />
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
                    <Button size='sm' variant='ghost' onClick={() => void confirm({ title: `删除《${d.title}》？`, confirmLabel: '删除' }).then((ok) => ok && remove.mutate(d.id))}>
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
