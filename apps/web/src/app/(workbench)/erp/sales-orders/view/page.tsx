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
import { OrderLinesTable } from '@/features/erp/order-lines-table';
import { SalesOrderFormDialog } from '@/features/erp/sales-order-form-dialog';
import { api } from '@/lib/api';
import { erpSalesOrderStatus, formatDate, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

type Transition = 'confirm' | 'ship' | 'cancel';

function SalesOrderDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
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

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const order = detail.data;
  const manage = can('erp.sales');
  const busy = transition.isPending || remove.isPending;

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
            {order.status === 'confirmed' ? (
              <Button
                onClick={() => confirm(`从「${order.warehouseName}」发货并扣减库存？发货后不能撤销。`) && transition.mutate('ship')}
                disabled={busy}
              >
                <Icons.send />
                发货
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'confirmed' ? (
              <Button
                variant='ghost'
                onClick={() => confirm(order.status === 'confirmed' ? '取消订单并释放预留的库存？' : '取消这张订单？') && transition.mutate('cancel')}
                disabled={busy}
              >
                <Icons.circleX />
                取消订单
              </Button>
            ) : null}
            {order.status === 'draft' || order.status === 'cancelled' ? (
              <Button variant='ghost' onClick={() => confirm(`删除订单 ${order.number}？`) && remove.mutate()} disabled={busy}>
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
              <OrderLinesTable lines={order.lines} currency={order.currency} total={order.total} />
            </CardContent>
          </Card>
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
