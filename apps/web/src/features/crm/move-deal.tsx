'use client';

import { useState } from 'react';
import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query';
import type { CrmDealDto, CrmPipelineDto, CrmStageDto } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';

export interface MoveRequest {
  deal: CrmDealDto;
  stage: CrmStageDto;
  note?: string;
}

/**
 * 推进商机。传入看板的查询键时先在本地把卡片挪过去（乐观更新），失败再挪回来。
 */
export function useMoveDeal(pipelineKey?: QueryKey) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ deal, stage, note }: MoveRequest) => api.crm.deals.move(deal.id, { stageId: stage.id, note }),
    onMutate: async ({ deal, stage }) => {
      if (!pipelineKey) {
        return undefined;
      }

      await queryClient.cancelQueries({ queryKey: pipelineKey });
      const previous = queryClient.getQueryData<CrmPipelineDto>(pipelineKey);
      if (previous) {
        const moved = { ...deal, stageId: stage.id, stageName: stage.name, probability: stage.probability };
        queryClient.setQueryData<CrmPipelineDto>(pipelineKey, {
          ...previous,
          stages: previous.stages.map((column) => {
            const without = column.deals.filter((d) => d.id !== deal.id);
            if (column.stage.id === stage.id) {
              return { ...column, dealCount: column.dealCount + 1, deals: [moved, ...without] };
            }

            return without.length === column.deals.length ? column : { ...column, dealCount: column.dealCount - 1, deals: without };
          }),
        });
      }

      return { previous };
    },
    onError: (_error, _request, context) => {
      if (pipelineKey && context?.previous) {
        queryClient.setQueryData(pipelineKey, context.previous);
      }
    },
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['crm'] }),
  });
}

/** 赢单、输单时请对方写下原因（可留空），会记入阶段变化。 */
export function CloseDealDialog({ request, onCancel, onConfirm }: { request: MoveRequest | null; onCancel: () => void; onConfirm: (note: string) => void }) {
  return (
    <Dialog open={request !== null} onOpenChange={(open) => !open && onCancel()}>
      <DialogContent className='sm:max-w-md'>
        {request ? <CloseDealForm key={`${request.deal.id}:${request.stage.id}`} request={request} onCancel={onCancel} onConfirm={onConfirm} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function CloseDealForm({ request, onCancel, onConfirm }: { request: MoveRequest; onCancel: () => void; onConfirm: (note: string) => void }) {
  const [note, setNote] = useState('');
  const won = request.stage.kind === 'won';

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        onConfirm(note.trim());
      }}
    >
      <DialogHeader>
        <DialogTitle>
          {won ? '赢单' : '输单'}：{request.deal.title}
        </DialogTitle>
        <DialogDescription>移到「{request.stage.name}」。说明会记入跟进记录，方便以后复盘。</DialogDescription>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='close-note'>{won ? '赢单原因' : '输单原因'}（可选）</Label>
        <Textarea
          id='close-note'
          value={note}
          onChange={(e) => setNote(e.target.value)}
          rows={3}
          placeholder={won ? '例如：价格和交付周期都满足要求' : '例如：预算削减，项目推迟到明年'}
          autoFocus
        />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onCancel}>
          取消
        </Button>
        <Button type='submit'>确定</Button>
      </DialogFooter>
    </form>
  );
}
