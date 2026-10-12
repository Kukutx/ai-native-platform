'use client';

import { useDeferredValue, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ErpSalesOrderStatus } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Pager } from '@/features/crm/pager';
import { ErpNav } from '@/features/erp/erp-nav';
import { SalesOrderFormDialog } from '@/features/erp/sales-order-form-dialog';
import { api } from '@/lib/api';
import { erpSalesOrderStatus, formatAmount, formatDate } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;
const ALL = '__all__';
const PENDING = 'pending';

export default function SalesOrdersPage() {
  const { can, session } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<ErpSalesOrderStatus | typeof PENDING | undefined>();
  const [mine, setMine] = useState(false);
  const [page, setPage] = useState(1);
  const term = useDeferredValue(search.trim());
  const owner = mine ? session?.user.id : undefined;
  const orders = useQuery({
    queryKey: queryKeys.erpSalesOrders({ search: term, status, owner, page }),
    queryFn: ({ signal }) =>
      api.erp.salesOrders.list(term || undefined, status === PENDING ? undefined : status, undefined, undefined, owner, page, PAGE_SIZE, status === PENDING, signal),
    placeholderData: keepPreviousData,
  });
  const filtered = !!term || !!status || mine;

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='销售订单'
        description='确认时预留库存，可以分批发货、登记客户退货；可以从商机直接生成。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder='搜索单号或客户'
              className='w-48'
            />
            <Select
              value={status ?? ALL}
              onValueChange={(v) => {
                setStatus(v === ALL ? undefined : (v as ErpSalesOrderStatus | typeof PENDING));
                setPage(1);
              }}
            >
              <SelectTrigger className='w-28'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={ALL}>全部状态</SelectItem>
                <SelectItem value={PENDING}>未发完</SelectItem>
                {Object.entries(erpSalesOrderStatus).map(([key, meta]) => (
                  <SelectItem key={key} value={key}>
                    {meta.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select
              value={mine ? 'mine' : 'all'}
              onValueChange={(v) => {
                setMine(v === 'mine');
                setPage(1);
              }}
            >
              <SelectTrigger className='w-28'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value='all'>全部订单</SelectItem>
                <SelectItem value='mine'>我负责的</SelectItem>
              </SelectContent>
            </Select>
            {can('erp.sales') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建订单
              </Button>
            ) : null}
          </>
        }
      />
      <ErpNav />
      {orders.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : orders.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>单号</TableHead>
                  <TableHead>客户</TableHead>
                  <TableHead>下单日期</TableHead>
                  <TableHead>状态</TableHead>
                  <TableHead className='text-right'>金额</TableHead>
                  <TableHead>负责人</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {orders.data.items.map((o) => (
                  <TableRow key={o.id}>
                    <TableCell>
                      <Link href={`/erp/sales-orders/view?id=${o.id}`} className='font-mono font-medium hover:underline'>
                        {o.number}
                      </Link>
                    </TableCell>
                    <TableCell className='max-w-xs'>
                      <span className='block truncate'>{o.companyName ?? '—'}</span>
                      {o.dealTitle ? <span className='text-muted-foreground block truncate text-xs'>商机：{o.dealTitle}</span> : null}
                    </TableCell>
                    <TableCell className='text-muted-foreground'>{formatDate(o.orderDate)}</TableCell>
                    <TableCell>
                      <StatusBadge meta={erpSalesOrderStatus[o.status]} />
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{formatAmount(o.total, o.currency)}</TableCell>
                    <TableCell className='text-muted-foreground'>{o.owner?.name ?? '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={orders.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState
          title={filtered ? '没有符合条件的订单' : '还没有销售订单'}
          description={filtered ? '换个条件试试。' : '新建订单，或者在商机详情里点“生成销售订单”。'}
        />
      )}
      <SalesOrderFormDialog open={creating} onOpenChange={setCreating} onSaved={(o) => router.push(`/erp/sales-orders/view?id=${o.id}`)} />
    </div>
  );
}
