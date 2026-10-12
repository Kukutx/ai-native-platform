'use client';

import { useQuery } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { api } from '@/lib/api';
import { erpStockDocumentKind, formatAmount, formatQuantity, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

/** 订单的出入库单：每次发货、收货、退货各一张，最新的在前。 */
export function StockDocumentList({ salesOrderId, purchaseOrderId }: { salesOrderId?: string; purchaseOrderId?: string }) {
  const documents = useQuery({
    queryKey: queryKeys.erpStockDocuments({ salesOrderId, purchaseOrderId }),
    queryFn: ({ signal }) => api.erp.stockDocuments.list(undefined, salesOrderId, purchaseOrderId, undefined, 1, 100, signal),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>出入库单</CardTitle>
      </CardHeader>
      <CardContent>
        {documents.isPending ? (
          <Skeleton className='h-16 w-full' />
        ) : documents.data?.items.length ? (
          <ul className='divide-y text-sm'>
            {documents.data.items.map((d) => {
              const returns = d.kind === 'sales_return' || d.kind === 'purchase_return';
              return (
                <li key={d.id} className='flex flex-wrap items-start gap-x-4 gap-y-1 py-3'>
                  <div className='min-w-0 flex-1 space-y-1'>
                    <p className='flex flex-wrap items-center gap-2'>
                      <span className='font-mono font-medium'>{d.number}</span>
                      <span className='text-muted-foreground'>{erpStockDocumentKind[d.kind]}</span>
                    </p>
                    <p className='text-muted-foreground text-xs'>{d.lines.map((l) => `${l.productName} × ${formatQuantity(l.quantity, l.unit)}`).join('、')}</p>
                    {d.note ? <p className='text-xs'>{d.note}</p> : null}
                    <p className='text-muted-foreground text-xs'>
                      {d.warehouseName} · {d.createdBy.name ?? '系统'} · {formatTime(d.createdAt)}
                    </p>
                  </div>
                  <span className={returns ? 'text-destructive shrink-0 tabular-nums' : 'shrink-0 tabular-nums'}>
                    {returns ? '−' : ''}
                    {formatAmount(d.amount, d.currency)}
                  </span>
                </li>
              );
            })}
          </ul>
        ) : (
          <p className='text-muted-foreground text-sm'>还没有出入库记录。</p>
        )}
      </CardContent>
    </Card>
  );
}
