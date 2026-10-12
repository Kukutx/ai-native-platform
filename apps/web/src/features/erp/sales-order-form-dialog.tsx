'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ErpSalesOrderDto } from '@ainative/api-client';
import { CurrencySelect } from '@/components/app/currency-select';
import { EditConflictAlert, isEditConflict, ReloadableForm, useEditVersion } from '@/components/app/edit-conflict';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { OwnerSelect } from '@/features/crm/owner-select';
import { RecordPicker, type PickedRecord } from '@/features/crm/record-picker';
import { ownerUserId } from '@/features/crm/utils';
import { api } from '@/lib/api';
import { today } from '@/lib/dates';
import { queryKeys } from '@/lib/query-keys';
import { fromOrderLine, OrderLinesEditor, toLineRequests, type LineDraft } from './order-lines-editor';
import { WarehouseSelect } from './warehouse-select';

/** 从商机生成订单时带入的内容。 */
export interface SalesOrderDefaults {
  deal?: { id: string; title: string; currency: string };
  company?: PickedRecord | null;
  contact?: PickedRecord | null;
}

/** order 为 null 时新建，否则编辑（只有草稿可以编辑）。 */
export function SalesOrderFormDialog({
  open,
  order,
  defaults,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  order?: ErpSalesOrderDto | null;
  defaults?: SalesOrderDefaults;
  onOpenChange: (open: boolean) => void;
  onSaved?: (order: ErpSalesOrderDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-3xl'>
        {open ? (
          <ReloadableForm entity={order ?? null}>
            {(current, reload, key) => (
              <SalesOrderForm key={key} order={current} defaults={defaults} onReload={reload} onClose={() => onOpenChange(false)} onSaved={onSaved} />
            )}
          </ReloadableForm>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function SalesOrderForm({
  order,
  defaults,
  onReload,
  onClose,
  onSaved,
}: {
  order: ErpSalesOrderDto | null;
  defaults?: SalesOrderDefaults;
  onReload: (latest: ErpSalesOrderDto) => void;
  onClose: () => void;
  onSaved?: (order: ErpSalesOrderDto) => void;
}) {
  const queryClient = useQueryClient();
  const { session } = useSession();
  const edit = useEditVersion(order, () => {
    const id = order!.id;
    return queryClient.fetchQuery({ queryKey: queryKeys.erpSalesOrder(id), queryFn: ({ signal }) => api.erp.salesOrders.get(id, signal), staleTime: 0 });
  });
  const [deal, setDeal] = useState(order?.dealId ? { id: order.dealId, title: order.dealTitle ?? '商机' } : defaults?.deal);
  const [company, setCompany] = useState<PickedRecord | null>(
    order ? (order.companyId ? { id: order.companyId, name: order.companyName ?? '客户' } : null) : (defaults?.company ?? null),
  );
  const [contact, setContact] = useState<PickedRecord | null>(
    order ? (order.contactId ? { id: order.contactId, name: order.contactName ?? '联系人' } : null) : (defaults?.contact ?? null),
  );
  const [warehouseId, setWarehouseId] = useState(order?.warehouseId);
  const [currency, setCurrency] = useState(order?.currency ?? defaults?.deal?.currency ?? 'CNY');
  const [orderDate, setOrderDate] = useState(order?.orderDate ?? today());
  const [note, setNote] = useState(order?.note ?? '');
  const [owner, setOwner] = useState(order ? ownerUserId(order.owner) : session?.user.id);
  const [lines, setLines] = useState<LineDraft[]>(order?.lines.map(fromOrderLine) ?? []);
  const save = useMutation({
    mutationFn: (confirm: boolean) => {
      const request = {
        dealId: deal?.id,
        companyId: company?.id,
        contactId: contact?.id,
        warehouseId,
        currency,
        orderDate: orderDate || undefined,
        note,
        ownerUserId: owner,
        lines: toLineRequests(lines),
        expectedUpdatedAt: edit.expected(),
      };
      return order ? api.erp.salesOrders.update(order.id, request) : api.erp.salesOrders.create(request, confirm);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      onSaved?.(saved);
      onClose();
    },
    meta: { handledError: isEditConflict },
    onError: (error) => {
      edit.onError(error);
      void queryClient.invalidateQueries({ queryKey: ['erp'] });
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        // 回车提交按"保存"处理；"保存并确认"由按钮单独触发。
        const submitter = (event.nativeEvent as SubmitEvent).submitter as HTMLButtonElement | null;
        save.mutate(submitter?.value === 'confirm');
      }}
    >
      <DialogHeader>
        <DialogTitle>{order ? `编辑销售订单 ${order.number}` : '新建销售订单'}</DialogTitle>
      </DialogHeader>
      {deal ? (
        <div className='bg-muted/50 flex items-center gap-2 rounded-md px-3 py-2 text-sm'>
          <Icons.crm className='text-muted-foreground size-4' />
          <span className='text-muted-foreground'>关联商机</span>
          <span className='truncate font-medium'>{deal.title}</span>
          <Button type='button' size='icon' variant='ghost' className='ml-auto size-6' onClick={() => setDeal(undefined)} aria-label='不关联商机'>
            <Icons.close className='size-4' />
          </Button>
        </div>
      ) : null}
      <div className='grid gap-4 sm:grid-cols-3'>
        <div className='grid gap-2'>
          <Label>客户</Label>
          <RecordPicker
            kind='company'
            value={company}
            onChange={(next) => {
              setCompany(next);
              if (next?.id !== company?.id) {
                setContact(null);
              }
            }}
            placeholder={deal ? '留空则用商机的客户' : undefined}
          />
        </div>
        <div className='grid gap-2'>
          <Label>联系人</Label>
          <RecordPicker kind='contact' value={contact} onChange={setContact} companyId={company?.id} />
        </div>
        <OwnerSelect value={owner} onChange={setOwner} allowNone={!!order} />
        <div className='grid gap-2'>
          <Label>发货仓库</Label>
          <WarehouseSelect value={warehouseId} onChange={setWarehouseId} allowDefault />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='so-date'>下单日期</Label>
          <Input id='so-date' type='date' value={orderDate} onChange={(e) => setOrderDate(e.target.value)} />
        </div>
        <div className='grid gap-2'>
          <Label>货币</Label>
          <CurrencySelect value={currency} onChange={setCurrency} className='w-full' />
        </div>
      </div>
      <div className='grid gap-2'>
        <Label>明细</Label>
        <OrderLinesEditor lines={lines} onChange={setLines} currency={currency} priceOf={(p) => p.price} showAvailability />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='so-note'>备注</Label>
        <Textarea id='so-note' value={note} onChange={(e) => setNote(e.target.value)} rows={2} placeholder='交付要求、开票信息等' />
      </div>
      <EditConflictAlert edit={edit} subject='销售订单' onLoadLatest={onReload} onOverwrite={() => save.mutate(false)} disabled={save.isPending} />
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' value='save' variant={order ? 'default' : 'outline'} disabled={save.isPending || !lines.length}>
          {order ? '保存' : '保存草稿'}
        </Button>
        {order ? null : (
          <Button type='submit' value='confirm' disabled={save.isPending || !lines.length} title='保存后立即预留库存，库存不够时不会保存'>
            保存并确认
          </Button>
        )}
      </DialogFooter>
    </form>
  );
}
