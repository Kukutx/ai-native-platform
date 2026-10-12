'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { WorkItemDetailDto, WorkItemDto } from '@ainative/api-client';
import { EditConflictAlert, isEditConflict, useEditVersion } from '@/components/app/edit-conflict';
import { fieldError, fieldErrorsOf, FieldMessage } from '@/components/app/field-errors';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardFooter } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Spinner } from '@/components/ui/spinner';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { updateRequest } from './task-update';

/**
 * 编辑任务的标题和描述。打开时记下任务的版本：期间被别人或智能体改过，保存时提示冲突，由用户选择载入最新或仍然覆盖。
 * 其他属性（状态、负责人等）以最新数据为准原样提交，不会被这里的旧值覆盖。
 */
export function TaskTextEditor({ item, onDone }: { item: WorkItemDto; onDone: () => void }) {
  const queryClient = useQueryClient();
  const [title, setTitle] = useState(item.title);
  const [description, setDescription] = useState(item.description);
  const detailKey = queryKeys.workItem(item.id);
  const edit = useEditVersion(item, async () => {
    const latest = await queryClient.fetchQuery({ queryKey: detailKey, queryFn: ({ signal }) => api.workItems.get(item.id, signal), staleTime: 0 });
    return latest.item;
  });
  const save = useMutation({
    mutationFn: () => {
      const latest = queryClient.getQueryData<WorkItemDetailDto>(detailKey)?.item ?? item;
      return api.workItems.update(item.id, updateRequest(latest, { title: title.trim(), description: description.trim(), expectedUpdatedAt: edit.expected() }));
    },
    meta: { handledError: isEditConflict, successMessage: '已保存' },
    onError: edit.onError,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: detailKey });
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      onDone();
    },
  });
  const errors = fieldErrorsOf(save.error);

  return (
    <Card>
      <form
        className='contents'
        onSubmit={(event) => {
          event.preventDefault();
          save.mutate();
        }}
        onKeyDown={(event) => {
          if (event.key === 'Escape' && !save.isPending) {
            onDone();
          }
        }}
      >
        <CardContent className='grid gap-4'>
          <div className='grid gap-2'>
            <Label htmlFor='task-edit-title'>标题</Label>
            <Input id='task-edit-title' value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={300} autoFocus />
            <FieldMessage message={fieldError(errors, 'title')} />
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='task-edit-description'>描述</Label>
            <Textarea
              id='task-edit-description'
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={10}
              maxLength={50_000}
              placeholder='要做什么、验收标准、相关链接。支持 Markdown。'
            />
            <FieldMessage message={fieldError(errors, 'description')} />
          </div>
          <EditConflictAlert
            edit={edit}
            subject='任务'
            onLoadLatest={(latest) => {
              setTitle(latest.title);
              setDescription(latest.description);
            }}
            onOverwrite={() => save.mutate()}
            disabled={save.isPending}
          />
        </CardContent>
        <CardFooter className='justify-end gap-2'>
          <Button type='button' variant='outline' disabled={save.isPending} onClick={onDone}>
            取消
          </Button>
          <Button type='submit' disabled={save.isPending || !title.trim()}>
            {save.isPending ? <Spinner /> : null}
            保存
          </Button>
        </CardFooter>
      </form>
    </Card>
  );
}
