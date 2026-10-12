'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ErpPurchaseOrderDto } from '@ainative/api-client';
import { CurrencySelect } from '@/components/app/currency-select';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { RecordPicker, type PickedRecord } from '@/features/crm/record-picker';
import { api } from '@/lib/api';
import { fromOrderLine, OrderLinesEditor, toLineRequests, type LineDraft } from './order-lines-editor';
import { today } from './utils';
import { WarehouseSelect } from './warehouse-select';

/** order 为 null 时新建，否则编辑（只有草稿可以编辑）。供应商就是客户管理里的客户（公司或组织）。 */
export function PurchaseOrderFormDialog({
  open,
  order,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  order?: ErpPurchaseOrderDto | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (order: ErpPurchaseOrderDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-3xl'>
        {open ? <PurchaseOrderForm key={order?.id ?? 'new'} order={order ?? null} onClose={() => onOpenChange(false)} onSaved={onSaved} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function PurchaseOrderForm({ order, onClose, onSaved }: { order: ErpPurchaseOrderDto | null; onClose: () => void; onSaved?: (order: ErpPurchaseOrderDto) => void }) {
  const queryClient = useQueryClient();
  const [supplier, setSupplier] = useState<PickedRecord | null>(order?.supplierId ? { id: order.supplierId, name: order.supplierName ?? '供应商' } : null);
  const [warehouseId, setWarehouseId] = useState(order?.warehouseId);
  const [currency, setCurrency] = useState(order?.currency ?? 'CNY');
  const [orderDate, setOrderDate] = useState(order?.orderDate ?? today());
  const [expectedDate, setExpectedDate] = useState(order?.expectedDate ?? '');
  const [note, setNote] = useState(order?.note ?? '');
  const [lines, setLines] = useState<LineDraft[]>(order?.lines.map(fromOrderLine) ?? []);
  const save = useMutation({
    mutationFn: () => {
      const request = {
        supplierId: supplier?.id ?? '',
        warehouseId,
        currency,
        orderDate: orderDate || undefined,
        expectedDate: expectedDate || undefined,
        note,
        lines: toLineRequests(lines),
        expectedUpdatedAt: order?.updatedAt,
      };
      return order ? api.erp.purchaseOrders.update(order.id, request) : api.erp.purchaseOrders.create(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      onSaved?.(saved);
      onClose();
    },
    onError: () => queryClient.invalidateQueries({ queryKey: ['erp'] }),
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
        <DialogTitle>{order ? `编辑采购订单 ${order.number}` : '新建采购订单'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-4 sm:grid-cols-3'>
        <div className='grid gap-2 sm:col-span-2'>
          <Label>供应商</Label>
          <RecordPicker kind='company' value={supplier} onChange={setSupplier} placeholder='搜索供应商（客户管理里的公司）' />
        </div>
        <div className='grid gap-2'>
          <Label>入库仓库</Label>
          <WarehouseSelect value={warehouseId} onChange={setWarehouseId} allowDefault />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='po-date'>下单日期</Label>
          <Input id='po-date' type='date' value={orderDate} onChange={(e) => setOrderDate(e.target.value)} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='po-expected'>预计到货</Label>
          <Input id='po-expected' type='date' value={expectedDate} onChange={(e) => setExpectedDate(e.target.value)} />
        </div>
        <div className='grid gap-2'>
          <Label>货币</Label>
          <CurrencySelect value={currency} onChange={setCurrency} className='w-full' />
        </div>
      </div>
      <div className='grid gap-2'>
        <Label>明细</Label>
        <OrderLinesEditor lines={lines} onChange={setLines} currency={currency} priceOf={(p) => p.cost} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='po-note'>备注</Label>
        <Textarea id='po-note' value={note} onChange={(e) => setNote(e.target.value)} rows={2} />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || !supplier || !lines.length}>
          {order ? '保存' : '保存草稿'}
        </Button>
      </DialogFooter>
    </form>
  );
}
