'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { DetailState } from '@/components/app/query-state';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { InfoRow } from '@/components/app/info-row';
import { DeliveryDialog, type DeliveryMode } from '@/features/erp/delivery-dialog';
import { OrderLinesTable } from '@/features/erp/order-lines-table';
import { SalesOrderFormDialog } from '@/features/erp/sales-order-form-dialog';
import { StockDocumentList } from '@/features/erp/stock-document-list';
import { useDocumentTitle } from '@/hooks/use-document-title';
import { api } from '@/lib/api';
import { erpSalesOrderStatus, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

type Transition = 'confirm' | 'close' | 'cancel';

function SalesOrderDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const [editing, setEditing] = useState(false);
  const [delivery, setDelivery] = useState<DeliveryMode | null>(null);
  const detail = useQuery({ queryKey: queryKeys.erpSalesOrder(id), queryFn: ({ signal }) => api.erp.salesOrders.get(id, signal), enabled: !!id });
  const transition = useMutation({
    mutationFn: (action: Transition) => api.erp.salesOrders[action](id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['erp'] }),
  });
  const remove = useMutation({
    mutationFn: () => api.erp.salesOrders.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      router.replace('/erp/sales-orders');
    },
  });

  useDocumentTitle(detail.data ? `销售订单 ${detail.data.number}` : '销售订单');

  if (!detail.data) {
    return <DetailState id={id} query={detail} subject='销售订单' back={{ href: '/erp/sales-orders', label: '销售订单' }} />;
  }

  const order = detail.data;
  const manage = can('erp.sales');
  const busy = transition.isPending || remove.isPending;
  const started = order.status === 'partially_shipped' || order.status === 'shipped';
  const margin = order.grossProfit !== undefined && order.shippedAmount > 0 ? order.grossProfit / order.shippedAmount : undefined;

  return (
    <div className='space-y-6 p-6'>
      <Link href='/erp/sales-orders' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        销售订单
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <div className='flex items-center gap-2'>
            <h1 className='font-mono text-2xl font-semibold tracking-tight'>{order.number}</h1>
            <StatusBadge meta={erpSalesOrderStatus[order.status]} />
          </div>
          <div className='text-muted-foreground flex flex-wrap items-center gap-3 text-sm'>
            {order.companyId ? (
              <Link href={`/crm/companies/view?id=${order.companyId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.building className='size-4' />
                {order.companyName}
              </Link>
            ) : (
              <span>未指定客户</span>
            )}
            {order.contactId ? (
              <Link href={`/crm/contacts/view?id=${order.contactId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.user className='size-4' />
                {order.contactName}
              </Link>
            ) : null}
            {order.dealId ? (
              <Link href={`/crm/deals/view?id=${order.dealId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.crm className='size-4' />
                {order.dealTitle}
              </Link>
            ) : null}
          </div>
        </div>
        {manage ? (
          <div className='flex flex-wrap gap-2'>
            {order.status === 'draft' ? (
              <>
                <Button variant='outline' onClick={() => setEditing(true)} disabled={busy}>
                  <Icons.edit />
                  编辑
                </Button>
                <Button onClick={() => transition.mutate('confirm')} disabled={busy} title='按明细预留库存，库存不够时不会确认'>
                  <Icons.check />
                  确认订单
                </Button>
              </>
            ) : null}
            {order.status === 'confirmed' || order.status === 'partially_shipped' ? (
              <Button onClick={() => setDelivery('ship')} disabled={busy} title='可以分批发货'>
                <Icons.truck />
                发货
              </Button>
            ) : null}
            {order.status === 'partially_shipped' ? (
              <Button
                variant='outline'
                onClick={() =>
                  void confirm({
                    title: '结束发货？',
                    description: '剩下没发的不再发货，释放预留的库存，订单记为已发货。',
                    confirmLabel: '结束发货',
                    destructive: false,
                  }).then((ok) => ok && transition.mutate('close'))
                }
                disabled={busy}
              >
                <Icons.circleCheck />
                结束发货
              </Button>
            ) : null}
            {started ? (
              <Button variant='outline' onClick={() => setDelivery('sales_return')} disabled={busy}>
                <Icons.undo />
                退货
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'confirmed' ? (
              <Button
                variant='ghost'
                onClick={() =>
                  void confirm({
                    title: order.status === 'confirmed' ? '取消订单并释放预留的库存？' : '取消这张订单？',
                    confirmLabel: '取消订单',
                    cancelLabel: '不取消',
                  }).then((ok) => ok && transition.mutate('cancel'))
                }
                disabled={busy}
              >
                <Icons.circleX />
                取消订单
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'cancelled' ? (
              <Button variant='ghost' onClick={() => void confirm({ title: `删除订单 ${order.number}？`, confirmLabel: '删除' }).then((ok) => ok && remove.mutate())} disabled={busy}>
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
              <OrderLinesTable lines={order.lines} currency={order.currency} total={order.total} deliveredLabel={started ? '已发' : undefined} />
            </CardContent>
          </Card>
          {started ? <StockDocumentList salesOrderId={order.id} /> : null}
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
            <InfoRow icon={Icons.user} label='负责人'>
              {order.owner?.name ?? null}
            </InfoRow>
            {started ? (
              <div className='space-y-3 border-t pt-3'>
                <InfoRow icon={Icons.truck} label='已发货'>
                  <span className='tabular-nums'>{formatAmount(order.shippedAmount, order.currency)}</span>
                </InfoRow>
                <InfoRow icon={Icons.product} label='成本'>
                  {order.costOfGoods !== undefined ? <span className='tabular-nums'>{formatAmount(order.costOfGoods, order.currency)}</span> : null}
                </InfoRow>
                <InfoRow icon={Icons.trendingUp} label='毛利'>
                  {order.grossProfit !== undefined ? (
                    <span className='tabular-nums'>
                      {formatAmount(order.grossProfit, order.currency)}
                      {margin !== undefined ? <span className='text-muted-foreground'> · {(margin * 100).toFixed(1)}%</span> : null}
                    </span>
                  ) : (
                    <span className='text-muted-foreground text-xs'>有产品的货币与订单不同，暂不计算</span>
                  )}
                </InfoRow>
              </div>
            ) : null}
            <div className='text-muted-foreground space-y-1 border-t pt-3 text-xs'>
              <p>
                {order.createdBy.name ?? '系统'} 创建于 {formatTime(order.createdAt)}
              </p>
              {order.confirmedAt ? <p>确认于 {formatTime(order.confirmedAt)}</p> : null}
              {order.shippedAt ? <p>发货于 {formatTime(order.shippedAt)}</p> : null}
              {order.cancelledAt ? <p>取消于 {formatTime(order.cancelledAt)}</p> : null}
            </div>
          </CardContent>
        </Card>
      </div>
      <SalesOrderFormDialog open={editing} order={order} onOpenChange={setEditing} />
      <DeliveryDialog open={!!delivery} mode={delivery ?? 'ship'} order={order} onOpenChange={(open) => !open && setDelivery(null)} />
    </div>
  );
}

export default function SalesOrderDetailPage() {
  return (
    <Suspense>
      <SalesOrderDetail />
    </Suspense>
  );
}
