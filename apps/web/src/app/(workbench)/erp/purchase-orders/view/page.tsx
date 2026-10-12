'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { InfoRow } from '@/features/crm/info-row';
import { DeliveryDialog, type DeliveryMode } from '@/features/erp/delivery-dialog';
import { OrderLinesTable } from '@/features/erp/order-lines-table';
import { PurchaseOrderFormDialog } from '@/features/erp/purchase-order-form-dialog';
import { StockDocumentList } from '@/features/erp/stock-document-list';
import { api } from '@/lib/api';
import { erpPurchaseOrderStatus, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

type Transition = 'place' | 'close' | 'cancel';

function PurchaseOrderDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [delivery, setDelivery] = useState<DeliveryMode | null>(null);
  const detail = useQuery({ queryKey: queryKeys.erpPurchaseOrder(id), queryFn: ({ signal }) => api.erp.purchaseOrders.get(id, signal), enabled: !!id });
  const transition = useMutation({
    mutationFn: (action: Transition) => api.erp.purchaseOrders[action](id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['erp'] }),
  });
  const remove = useMutation({
    mutationFn: () => api.erp.purchaseOrders.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      router.replace('/erp/purchase-orders');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const order = detail.data;
  const manage = can('erp.purchase');
  const busy = transition.isPending || remove.isPending;
  const started = order.status === 'partially_received' || order.status === 'received';

  return (
    <div className='space-y-6 p-6'>
      <Link href='/erp/purchase-orders' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        采购订单
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <div className='flex items-center gap-2'>
            <h1 className='font-mono text-2xl font-semibold tracking-tight'>{order.number}</h1>
            <StatusBadge meta={erpPurchaseOrderStatus[order.status]} />
          </div>
          {order.supplierId ? (
            <Link href={`/crm/companies/view?id=${order.supplierId}`} className='inline-flex items-center gap-1 text-sm hover:underline'>
              <Icons.building className='size-4' />
              {order.supplierName}
            </Link>
          ) : null}
        </div>
        {manage ? (
          <div className='flex flex-wrap gap-2'>
            {order.status === 'draft' ? (
              <>
                <Button variant='outline' onClick={() => setEditing(true)} disabled={busy}>
                  <Icons.edit />
                  编辑
                </Button>
                <Button onClick={() => transition.mutate('place')} disabled={busy}>
                  <Icons.send />
                  下单
                </Button>
              </>
            ) : null}
            {order.status === 'ordered' || order.status === 'partially_received' ? (
              <Button onClick={() => setDelivery('receive')} disabled={busy} title='货没到齐时可以分批收货'>
                <Icons.check />
                收货入库
              </Button>
            ) : null}
            {order.status === 'partially_received' ? (
              <Button variant='outline' onClick={() => confirm('剩下没到的不再收货，采购单记为已入库？') && transition.mutate('close')} disabled={busy}>
                <Icons.circleCheck />
                结束收货
              </Button>
            ) : null}
            {started ? (
              <Button variant='outline' onClick={() => setDelivery('purchase_return')} disabled={busy}>
                <Icons.undo />
                退货
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'ordered' ? (
              <Button variant='ghost' onClick={() => confirm('取消这张采购单？') && transition.mutate('cancel')} disabled={busy}>
                <Icons.circleX />
                取消
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'cancelled' ? (
              <Button variant='ghost' onClick={() => confirm(`删除采购单 ${order.number}？`) && remove.mutate()} disabled={busy}>
                <Icons.trash />
                删除
              </Button>
            ) : null}
          </div>
        ) : null}
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          <Card>
            <CardHeader>
              <CardTitle>明细</CardTitle>
            </CardHeader>
            <CardContent>
              <OrderLinesTable lines={order.lines} currency={order.currency} total={order.total} deliveredLabel={started ? '已收' : undefined} />
            </CardContent>
          </Card>
          {started ? <StockDocumentList purchaseOrderId={order.id} /> : null}
          {order.note ? (
            <Card>
              <CardContent className='text-sm whitespace-pre-wrap'>{order.note}</CardContent>
            </Card>
          ) : null}
        </div>
        <Card className='h-fit'>
          <CardHeader>
            <CardTitle>详情</CardTitle>
          </CardHeader>
          <CardContent className='space-y-3 text-sm'>
            <InfoRow icon={Icons.building} label='仓库'>
              {order.warehouseName}
            </InfoRow>
            <InfoRow icon={Icons.calendar} label='下单'>
              {formatDate(order.orderDate)}
            </InfoRow>
            <InfoRow icon={Icons.clock} label='预计到货'>
              {order.expectedDate ? formatDate(order.expectedDate) : null}
            </InfoRow>
            {started ? (
              <InfoRow icon={Icons.receipt} label='已入库'>
                <span className='tabular-nums'>{formatAmount(order.receivedAmount, order.currency)}</span>
              </InfoRow>
            ) : null}
            <div className='text-muted-foreground space-y-1 border-t pt-3 text-xs'>
              <p>
                {order.createdBy.name ?? '系统'} 创建于 {formatTime(order.createdAt)}
              </p>
              {order.orderedAt ? <p>下单于 {formatTime(order.orderedAt)}</p> : null}
              {order.receivedAt ? <p>入库于 {formatTime(order.receivedAt)}</p> : null}
              {order.cancelledAt ? <p>取消于 {formatTime(order.cancelledAt)}</p> : null}
            </div>
          </CardContent>
        </Card>
      </div>
      <PurchaseOrderFormDialog open={editing} order={order} onOpenChange={setEditing} />
      <DeliveryDialog open={!!delivery} mode={delivery ?? 'receive'} order={order} onOpenChange={(open) => !open && setDelivery(null)} />
    </div>
  );
}

export default function PurchaseOrderDetailPage() {
  return (
    <Suspense>
      <PurchaseOrderDetail />
    </Suspense>
  );
}
