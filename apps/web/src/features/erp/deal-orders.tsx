'use client';

import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { StatusBadge } from '@/components/app/status-badge';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { api } from '@/lib/api';
import { erpSalesOrderStatus, formatAmount, formatDate } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

/** 商机详情里的销售订单列表；没有订单时不显示。 */
export function DealOrders({ dealId }: { dealId: string }) {
  const orders = useQuery({
    queryKey: queryKeys.erpSalesOrders({ dealId }),
    queryFn: ({ signal }) => api.erp.salesOrders.list(undefined, undefined, undefined, dealId, undefined, 1, 20, undefined, signal),
  });

  if (!orders.data?.items.length) {
    return null;
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>
          销售订单 <span className='text-muted-foreground font-normal'>{orders.data.total}</span>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <ul className='divide-y text-sm'>
          {orders.data.items.map((o) => (
            <li key={o.id} className='flex items-center gap-3 py-2'>
              <div className='min-w-0 flex-1'>
                <Link href={`/erp/sales-orders/view?id=${o.id}`} className='font-mono font-medium hover:underline'>
                  {o.number}
                </Link>
                <p className='text-muted-foreground text-xs'>
                  {formatDate(o.orderDate)} · {o.lines.length} 种产品
                </p>
              </div>
              <span className='tabular-nums'>{formatAmount(o.total, o.currency)}</span>
              <StatusBadge meta={erpSalesOrderStatus[o.status]} />
            </li>
          ))}
        </ul>
      </CardContent>
    </Card>
  );
}
