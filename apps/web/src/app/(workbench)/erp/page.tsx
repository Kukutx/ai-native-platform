'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { StatCard } from '@/components/app/stat-card';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { ErpNav } from '@/features/erp/erp-nav';
import { PurchaseOrderFormDialog } from '@/features/erp/purchase-order-form-dialog';
import { SalesOrderFormDialog } from '@/features/erp/sales-order-form-dialog';
import { api } from '@/lib/api';
import { formatAmount, formatDate, formatQuantity, formatTotals } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const LIST_SIZE = 8;

export default function ErpOverviewPage() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState<'sales' | 'purchase' | null>(null);
  const summary = useQuery({ queryKey: queryKeys.erpSummary, queryFn: ({ signal }) => api.erp.summary.get(signal) });
  const toShip = useQuery({
    queryKey: queryKeys.erpSalesOrders({ status: 'confirmed', size: LIST_SIZE }),
    queryFn: ({ signal }) => api.erp.salesOrders.list(undefined, 'confirmed', undefined, undefined, undefined, 1, LIST_SIZE, signal),
  });
  const incoming = useQuery({
    queryKey: queryKeys.erpPurchaseOrders({ status: 'ordered', size: LIST_SIZE }),
    queryFn: ({ signal }) => api.erp.purchaseOrders.list(undefined, 'ordered', undefined, 1, LIST_SIZE, signal),
  });
  const lowStock = useQuery({
    queryKey: queryKeys.erpProducts({ lowStockOnly: true, size: LIST_SIZE }),
    queryFn: ({ signal }) => api.erp.products.list(undefined, undefined, true, undefined, 1, LIST_SIZE, signal),
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='进销存'
        description='产品、库存和订单。销售订单确认时预留库存、发货时出库；采购订单收货时入库。'
        actions={
          <>
            {can('erp.purchase') ? (
              <Button variant='outline' onClick={() => setCreating('purchase')}>
                <Icons.add />
                采购订单
              </Button>
            ) : null}
            {can('erp.sales') ? (
              <Button onClick={() => setCreating('sales')}>
                <Icons.add />
                销售订单
              </Button>
            ) : null}
          </>
        }
      />
      <ErpNav />
      {summary.data ? (
        <div className='grid gap-4 sm:grid-cols-2 lg:grid-cols-4'>
          <StatCard label='本月发货' value={formatTotals(summary.data.shippedThisMonth)} />
          <StatCard
            label='待发货'
            value={`${summary.data.toShipSalesOrders} 单`}
            detail={summary.data.toShipSalesOrders ? formatTotals(summary.data.toShipTotals) : undefined}
          />
          <StatCard label='本月采购入库' value={formatTotals(summary.data.receivedThisMonth)} detail={`${summary.data.openPurchaseOrders} 张采购单待收货`} />
          <StatCard
            label='库存预警'
            hint='可用数量不高于补货提醒线的在售产品'
            value={`${summary.data.lowStockProducts} 个产品`}
            detail={`共 ${summary.data.activeProducts} 个在售产品`}
            tone={summary.data.lowStockProducts ? 'warning' : undefined}
          />
        </div>
      ) : (
        <Skeleton className='h-24 w-full' />
      )}
      <div className='grid gap-6 lg:grid-cols-3'>
        <ListCard title='待发货的销售订单' href='/erp/sales-orders' empty='没有待发货的订单。' loading={toShip.isPending}>
          {toShip.data?.items.map((o) => (
            <Row
              key={o.id}
              href={`/erp/sales-orders/view?id=${o.id}`}
              title={`${o.number} · ${o.companyName ?? '未指定客户'}`}
              hint={`下单 ${formatDate(o.orderDate)} · ${o.warehouseName}`}
              value={formatAmount(o.total, o.currency)}
            />
          ))}
        </ListCard>
        <ListCard title='待收货的采购订单' href='/erp/purchase-orders' empty='没有待收货的采购单。' loading={incoming.isPending}>
          {incoming.data?.items.map((o) => (
            <Row
              key={o.id}
              href={`/erp/purchase-orders/view?id=${o.id}`}
              title={`${o.number} · ${o.supplierName ?? '供应商'}`}
              hint={o.expectedDate ? `预计 ${formatDate(o.expectedDate)} 到货` : `下单 ${formatDate(o.orderDate)}`}
              value={formatAmount(o.total, o.currency)}
            />
          ))}
        </ListCard>
        <ListCard title='需要补货' href='/erp/inventory?low=1' empty='库存都在提醒线以上。' loading={lowStock.isPending}>
          {lowStock.data?.items.map((p) => (
            <Row
              key={p.id}
              href={`/erp/products/view?id=${p.id}`}
              title={p.name}
              hint={`${p.sku} · 提醒线 ${formatQuantity(p.reorderPoint, p.unit)}`}
              value={`可用 ${formatQuantity(p.available, p.unit)}`}
              warning
            />
          ))}
        </ListCard>
      </div>
      <SalesOrderFormDialog
        open={creating === 'sales'}
        onOpenChange={(open) => setCreating(open ? 'sales' : null)}
        onSaved={(o) => router.push(`/erp/sales-orders/view?id=${o.id}`)}
      />
      <PurchaseOrderFormDialog
        open={creating === 'purchase'}
        onOpenChange={(open) => setCreating(open ? 'purchase' : null)}
        onSaved={(o) => router.push(`/erp/purchase-orders/view?id=${o.id}`)}
      />
    </div>
  );
}

function ListCard({ title, href, empty, loading, children }: { title: string; href: string; empty: string; loading: boolean; children?: React.ReactNode[] }) {
  return (
    <Card>
      <CardHeader className='flex flex-row items-center justify-between'>
        <CardTitle>{title}</CardTitle>
        <Link href={href} className='text-muted-foreground hover:text-foreground text-xs'>
          查看全部
        </Link>
      </CardHeader>
      <CardContent>
        {loading ? (
          <Skeleton className='h-24 w-full' />
        ) : children?.length ? (
          <ul className='divide-y text-sm'>{children}</ul>
        ) : (
          <p className='text-muted-foreground text-sm'>{empty}</p>
        )}
      </CardContent>
    </Card>
  );
}

function Row({ href, title, hint, value, warning }: { href: string; title: string; hint: string; value: string; warning?: boolean }) {
  return (
    <li className='flex items-center gap-3 py-2'>
      <div className='min-w-0 flex-1'>
        <Link href={href} className='block truncate font-medium hover:underline'>
          {title}
        </Link>
        <p className='text-muted-foreground truncate text-xs'>{hint}</p>
      </div>
      <span className={warning ? 'shrink-0 text-xs font-medium text-amber-600 tabular-nums dark:text-amber-500' : 'shrink-0 tabular-nums'}>{value}</span>
    </li>
  );
}
