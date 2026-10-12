'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { CrmStageDto, CrmStageKind } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { api } from '@/lib/api';
import { crmStageKind } from '@/lib/labels';

interface StageRow {
  key: string;
  id?: string;
  name: string;
  probability: number;
  kind: CrmStageKind;
}

export function StageSettingsDialog({ open, stages, onOpenChange }: { open: boolean; stages: CrmStageDto[]; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-2xl'>{open ? <StageSettings stages={stages} onClose={() => onOpenChange(false)} /> : null}</DialogContent>
    </Dialog>
  );
}

function StageSettings({ stages, onClose }: { stages: CrmStageDto[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [rows, setRows] = useState<StageRow[]>(() => stages.map((s) => ({ key: s.id, id: s.id, name: s.name, probability: s.probability, kind: s.kind })));
  const save = useMutation({
    mutationFn: () => api.crm.stages.save({ stages: rows.map(({ id, name, probability, kind }) => ({ id, name, probability, kind })) }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      onClose();
    },
  });
  const update = (index: number, patch: Partial<StageRow>) => setRows(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)));
  const move = (index: number, offset: number) => {
    const next = [...rows];
    const [row] = next.splice(index, 1);
    next.splice(index + offset, 0, row);
    setRows(next);
  };

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>销售阶段</DialogTitle>
        <DialogDescription>
          看板按这里的顺序分列。概率用于计算加权预测金额；类型为赢单或输单的阶段表示商机已结束。还有商机的阶段不能删除，先把商机移走。
        </DialogDescription>
      </DialogHeader>
      <div className='grid gap-2'>
        <div className='text-muted-foreground grid grid-cols-[1fr_5.5rem_6.5rem_5.5rem] gap-2 px-1 text-xs'>
          <span>名称</span>
          <span>概率 %</span>
          <span>类型</span>
          <span />
        </div>
        {rows.map((row, index) => (
          <div key={row.key} className='grid grid-cols-[1fr_5.5rem_6.5rem_5.5rem] items-center gap-2'>
            <Input value={row.name} onChange={(e) => update(index, { name: e.target.value })} required maxLength={50} aria-label='阶段名称' />
            <Input
              type='number'
              min={0}
              max={100}
              value={row.probability}
              onChange={(e) => update(index, { probability: Math.min(100, Math.max(0, Number(e.target.value) || 0)) })}
              aria-label='成交概率'
            />
            <Select value={row.kind} onValueChange={(v) => update(index, { kind: v as CrmStageKind })}>
              <SelectTrigger className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {Object.entries(crmStageKind).map(([key, label]) => (
                  <SelectItem key={key} value={key}>
                    {label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <div className='flex'>
              <Button type='button' size='icon' variant='ghost' className='size-8' disabled={index === 0} onClick={() => move(index, -1)} aria-label='上移'>
                <Icons.arrowUp className='size-4' />
              </Button>
              <Button
                type='button'
                size='icon'
                variant='ghost'
                className='size-8'
                disabled={index === rows.length - 1}
                onClick={() => move(index, 1)}
                aria-label='下移'
              >
                <Icons.arrowDown className='size-4' />
              </Button>
              <Button
                type='button'
                size='icon'
                variant='ghost'
                className='size-8'
                disabled={rows.length === 1}
                onClick={() => setRows(rows.filter((_, i) => i !== index))}
                aria-label='删除'
              >
                <Icons.trash className='size-4' />
              </Button>
            </div>
          </div>
        ))}
        <Button
          type='button'
          variant='outline'
          size='sm'
          className='w-fit'
          disabled={rows.length >= 20}
          onClick={() => {
            // 新阶段默认插在第一个已结束阶段之前。
            const at = rows.findIndex((r) => r.kind !== 'open');
            const row: StageRow = { key: crypto.randomUUID(), name: '', probability: 50, kind: 'open' };
            setRows(at < 0 ? [...rows, row] : [...rows.slice(0, at), row, ...rows.slice(at)]);
          }}
        >
          <Icons.add />
          添加阶段
        </Button>
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
