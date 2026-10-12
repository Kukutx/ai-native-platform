'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ErpOrderLineDto, ErpQuantityLineRequest } from '@ainative/api-client';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { formatQuantity } from '@/lib/labels';
import { WarehouseSelect } from './warehouse-select';

export type DeliveryMode = 'ship' | 'receive' | 'sales_return' | 'purchase_return';

const modes: Record<DeliveryMode, { title: string; limit: string; submit: string; note: string; placeholder: string; returns: boolean }> = {
  ship: {
    title: '发货',
    limit: '未发',
    submit: '确认发货',
    note: '备注',
    placeholder: '例如：快递单号',
    returns: false,
  },
  receive: {
    title: '收货入库',
    limit: '未收',
    submit: '确认收货',
    note: '备注',
    placeholder: '例如：送货单号',
    returns: false,
  },
  sales_return: {
    title: '客户退货',
    limit: '可退',
    submit: '登记退货',
    note: '退货原因',
    placeholder: '例如：包装破损',
    returns: true,
  },
  purchase_return: {
    title: '退给供应商',
    limit: '可退',
    submit: '登记退货',
    note: '退货原因',
    placeholder: '例如：质量问题',
    returns: true,
  },
};

function description(mode: DeliveryMode, warehouseName: string) {
  switch (mode) {
    case 'ship':
      return `从「${warehouseName}」出库。可以只发一部分，没发的继续预留，之后再发。`;
    case 'receive':
      return `入库到「${warehouseName}」。货没到齐时只填到了的数量，之后再收。`;
    case 'sales_return':
      return '退回的货按发货时的成本入库，订单的发货金额和毛利相应冲减。';
    case 'purchase_return':
      return '按当前平均成本出库。只能退可用的数量，已预留给销售订单的不能退。';
  }
}

interface DeliveryOrder {
  id: string;
  warehouseId: string;
  warehouseName: string;
  lines: ErpOrderLineDto[];
}

/** 发货、收货、客户退货、退给供应商：逐行填写本次数量。发货和收货默认填满剩下的数量，退货默认为 0。 */
export function DeliveryDialog({
  open,
  mode,
  order,
  onOpenChange,
}: {
  open: boolean;
  mode: DeliveryMode;
  order: DeliveryOrder;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-xl'>{open ? <DeliveryForm mode={mode} order={order} onClose={() => onOpenChange(false)} /> : null}</DialogContent>
    </Dialog>
  );
}

function DeliveryForm({ mode, order, onClose }: { mode: DeliveryMode; order: DeliveryOrder; onClose: () => void }) {
  const queryClient = useQueryClient();
  const meta = modes[mode];
  const lines = order.lines
    .map((line) => ({ line, limit: meta.returns ? line.delivered - line.returned : line.quantity - line.delivered }))
    .filter((l) => l.limit > 0);
  const [quantities, setQuantities] = useState<Record<string, string>>(() =>
    Object.fromEntries(lines.map(({ line, limit }) => [line.productId, meta.returns ? '' : String(limit)])),
  );
  const [warehouseId, setWarehouseId] = useState<string | undefined>(order.warehouseId);
  const [note, setNote] = useState('');
  const picked: ErpQuantityLineRequest[] = lines
    .map(({ line }) => ({ productId: line.productId, quantity: Number(quantities[line.productId] || 0) }))
    .filter((l) => l.quantity > 0);
  const save = useMutation({
    mutationFn: (): Promise<unknown> => {
      switch (mode) {
        case 'ship':
          return api.erp.salesOrders.ship(order.id, { lines: picked, note });
        case 'receive':
          return api.erp.purchaseOrders.receive(order.id, { lines: picked, note });
        case 'sales_return':
          return api.erp.salesOrders.return(order.id, { lines: picked, warehouseId, note });
        case 'purchase_return':
          return api.erp.purchaseOrders.return(order.id, { lines: picked, warehouseId, note });
      }
    },
    onSuccess: async () => {
      toast.success(meta.returns ? '已登记退货' : mode === 'ship' ? '已发货' : '已入库');
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
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
        <DialogTitle>{meta.title}</DialogTitle>
        <DialogDescription>{description(mode, order.warehouseName)}</DialogDescription>
      </DialogHeader>
      {lines.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>产品</TableHead>
              <TableHead className='text-right'>{meta.limit}</TableHead>
              <TableHead className='w-32 text-right'>本次数量</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {lines.map(({ line, limit }) => (
              <TableRow key={line.productId}>
                <TableCell className='max-w-56'>
                  <span className='block truncate font-medium'>{line.productName}</span>
                  <span className='text-muted-foreground font-mono text-xs'>{line.sku}</span>
                </TableCell>
                <TableCell className='text-muted-foreground text-right tabular-nums'>{formatQuantity(limit, line.unit)}</TableCell>
                <TableCell>
                  <Input
                    type='number'
                    step='any'
                    min={0}
                    max={limit}
                    value={quantities[line.productId] ?? ''}
                    onChange={(e) => setQuantities((current) => ({ ...current, [line.productId]: e.target.value }))}
                    placeholder='0'
                    aria-label={`${line.productName} 本次数量`}
                    className='text-right'
                  />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <p className='text-muted-foreground text-sm'>{meta.returns ? '还没有可以退的数量。' : '已经全部交付。'}</p>
      )}
      {meta.returns ? (
        <div className='grid gap-2'>
          <Label>{mode === 'sales_return' ? '退回仓库' : '出库仓库'}</Label>
          <WarehouseSelect value={warehouseId} onChange={setWarehouseId} />
        </div>
      ) : null}
      <div className='grid gap-2'>
        <Label htmlFor='delivery-note'>{meta.note}</Label>
        <Input id='delivery-note' value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} placeholder={meta.placeholder} />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || !picked.length}>
          {meta.submit}
        </Button>
      </DialogFooter>
    </form>
  );
}
