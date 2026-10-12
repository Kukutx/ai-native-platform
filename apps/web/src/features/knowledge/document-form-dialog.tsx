'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { KnowledgeDocumentDto, SaveKnowledgeDocumentRequest } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';

export interface DocumentDraft {
  id?: string;
  title: string;
  content: string;
  fileName?: string;
  tags: string[];
  /** 从文件提取时的来源说明。 */
  source?: string;
  /** 提取时需要提醒的问题（截断、空白页、乱码等）。 */
  warnings?: string[];
}

export const emptyDraft: DocumentDraft = { title: '', content: '', tags: [] };

/** 标签输入：逗号、顿号或空格分隔。 */
function parseTags(text: string) {
  return [...new Set(text.split(/[,，、\s]+/).map((t) => t.trim()).filter(Boolean))];
}

export function DocumentFormDialog({
  draft,
  onClose,
  onSaved,
}: {
  draft: DocumentDraft | null;
  onClose: () => void;
  onSaved?: (document: KnowledgeDocumentDto) => void;
}) {
  return (
    <Dialog open={draft !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent className='max-h-[90vh] overflow-y-auto sm:max-w-2xl'>
        {draft ? <DocumentForm key={draft.id ?? draft.fileName ?? 'new'} draft={draft} onClose={onClose} onSaved={onSaved} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function DocumentForm({ draft, onClose, onSaved }: { draft: DocumentDraft; onClose: () => void; onSaved?: (document: KnowledgeDocumentDto) => void }) {
  const queryClient = useQueryClient();
  const [title, setTitle] = useState(draft.title);
  const [content, setContent] = useState(draft.content);
  const [tags, setTags] = useState(draft.tags.join('，'));
  const save = useMutation({
    mutationFn: () => {
      const request: SaveKnowledgeDocumentRequest = { title, content, fileName: draft.fileName, tags: parseTags(tags) };
      return draft.id ? api.knowledge.updateDocument(draft.id, request) : api.knowledge.createDocument(request);
    },
    onSuccess: async (document) => {
      await queryClient.invalidateQueries({ queryKey: ['knowledge'] });
      onSaved?.(document);
      onClose();
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{draft.id ? '编辑文档' : '新建文档'}</DialogTitle>
        <DialogDescription>{draft.source ?? '保存后立即可以检索；支持 Markdown，标题层级会作为片段的上下文。'}</DialogDescription>
      </DialogHeader>
      {draft.warnings?.length ? (
        <Alert>
          <Icons.warning />
          <AlertTitle>提取时发现的问题</AlertTitle>
          <AlertDescription>
            <ul className='list-disc space-y-0.5 pl-4'>
              {draft.warnings.map((warning) => (
                <li key={warning}>{warning}</li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : null}
      <div className='grid gap-2'>
        <Label htmlFor='doc-title'>标题</Label>
        <Input id='doc-title' value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={300} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='doc-tags'>标签</Label>
        <Input id='doc-tags' value={tags} onChange={(e) => setTags(e.target.value)} placeholder='用逗号分隔，例如：财务，制度' />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='doc-content'>正文</Label>
        <Textarea
          id='doc-content'
          value={content}
          onChange={(e) => setContent(e.target.value)}
          required
          rows={14}
          className='max-h-[50vh] font-mono text-sm'
        />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          保存
        </Button>
      </DialogFooter>
    </form>
  );
}
