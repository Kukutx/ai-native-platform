'use client';

import { useDeferredValue, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ErpPurchaseOrderStatus } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Pager } from '@/components/app/pager';
import { ErpNav } from '@/features/erp/erp-nav';
import { PurchaseOrderFormDialog } from '@/features/erp/purchase-order-form-dialog';
import { api } from '@/lib/api';
import { erpPurchaseOrderStatus, formatAmount, formatDate } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;
const ALL = '__all__';
const PENDING = 'pending';

export default function PurchaseOrdersPage() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<ErpPurchaseOrderStatus | typeof PENDING | undefined>();
  const [page, setPage] = useState(1);
  const term = useDeferredValue(search.trim());
  const orders = useQuery({
    queryKey: queryKeys.erpPurchaseOrders({ search: term, status, page }),
    queryFn: ({ signal }) =>
      api.erp.purchaseOrders.list(term || undefined, status === PENDING ? undefined : status, undefined, page, PAGE_SIZE, status === PENDING, signal),
    placeholderData: keepPreviousData,
  });
  const filtered = !!term || !!status;

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='采购订单'
        description='向供应商下单，到货后收货入库（可以分批收货、退货给供应商）。供应商来自客户管理里的公司。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder='搜索单号或供应商'
              className='w-48'
            />
            <Select
              value={status ?? ALL}
              onValueChange={(v) => {
                setStatus(v === ALL ? undefined : (v as ErpPurchaseOrderStatus | typeof PENDING));
                setPage(1);
              }}
            >
              <SelectTrigger className='w-28'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>全部状态</SelectItem>
                <SelectItem value={PENDING}>未收完</SelectItem>
                {Object.entries(erpPurchaseOrderStatus).map(([key, meta]) => (
                  <SelectItem key={key} value={key}>
                    {meta.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {can('erp.purchase') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建采购单
              </Button>
            ) : null}
          </>
        }
      />
      <ErpNav />
      {orders.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : orders.isError && !orders.data ? (
        <ErrorState query={orders} />
      ) : orders.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>单号</TableHead>
                  <TableHead>供应商</TableHead>
                  <TableHead>下单日期</TableHead>
                  <TableHead>预计到货</TableHead>
                  <TableHead>状态</TableHead>
                  <TableHead className='text-right'>金额</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {orders.data.items.map((o) => (
                  <TableRow key={o.id}>
                    <TableCell>
                      <Link href={`/erp/purchase-orders/view?id=${o.id}`} className='font-mono font-medium hover:underline'>
                        {o.number}
                      </Link>
                    </TableCell>
                    <TableCell className='max-w-xs truncate'>{o.supplierName ?? '—'}</TableCell>
                    <TableCell className='text-muted-foreground'>{formatDate(o.orderDate)}</TableCell>
                    <TableCell className='text-muted-foreground'>{formatDate(o.expectedDate)}</TableCell>
                    <TableCell>
                      <StatusBadge meta={erpPurchaseOrderStatus[o.status]} />
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{formatAmount(o.total, o.currency)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={orders.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState title={filtered ? '没有符合条件的采购单' : '还没有采购订单'} description={filtered ? '换个条件试试。' : '库存不够时新建采购单，到货后点“收货入库”。'} />
      )}
      <PurchaseOrderFormDialog open={creating} onOpenChange={setCreating} onSaved={(o) => router.push(`/erp/purchase-orders/view?id=${o.id}`)} />
    </div>
  );
}
