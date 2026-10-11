'use client';

import { Suspense, useDeferredValue, useState } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ErpStockMovementKind } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Pager } from '@/features/crm/pager';
import { ErpNav } from '@/features/erp/erp-nav';
import { MovementTable } from '@/features/erp/movement-table';
import { AdjustStockDialog, TransferStockDialog } from '@/features/erp/stock-dialogs';
import { WarehouseSelect } from '@/features/erp/warehouse-select';
import { WarehousesDialog } from '@/features/erp/warehouses-dialog';
import { api } from '@/lib/api';
import { erpMovementKind, formatQuantity } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;
const ALL = '__all__';

function Inventory() {
  const params = useSearchParams();
  const { can } = useSession();
  const productId = params.get('productId') ?? undefined;
  const [tab, setTab] = useState(productId ? 'movements' : 'levels');
  const [warehouseId, setWarehouseId] = useState<string | undefined>();
  const [dialog, setDialog] = useState<'adjust' | 'transfer' | 'warehouses' | null>(null);
  const manage = can('erp.inventory');

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='库存'
        description='各仓库的现有、已预留和可用数量，以及每一笔出入库流水。'
        actions={
          <>
            <Button variant='outline' onClick={() => setDialog('warehouses')}>
              <Icons.building />
              仓库
            </Button>
            {manage ? (
              <>
                <Button variant='outline' onClick={() => setDialog('transfer')}>
                  <Icons.arrowRight />
                  调拨
                </Button>
                <Button onClick={() => setDialog('adjust')}>
                  <Icons.adjustments />
                  调整库存
                </Button>
              </>
            ) : null}
          </>
        }
      />
      <ErpNav />
      <Tabs value={tab} onValueChange={setTab}>
        <div className='flex flex-wrap items-center justify-between gap-3'>
          <TabsList>
            <TabsTrigger value='levels'>库存</TabsTrigger>
            <TabsTrigger value='movements'>流水</TabsTrigger>
          </TabsList>
          <WarehouseSelect value={warehouseId} onChange={setWarehouseId} allLabel='全部仓库' className='w-40' />
        </div>
        <TabsContent value='levels' className='mt-4'>
          <Levels warehouseId={warehouseId} lowOnly={params.get('low') === '1'} />
        </TabsContent>
        <TabsContent value='movements' className='mt-4'>
          <Movements warehouseId={warehouseId} productId={productId} />
        </TabsContent>
      </Tabs>
      <AdjustStockDialog open={dialog === 'adjust'} warehouseId={warehouseId} onOpenChange={(open) => !open && setDialog(null)} />
      <TransferStockDialog open={dialog === 'transfer'} fromWarehouseId={warehouseId} onOpenChange={(open) => !open && setDialog(null)} />
      <WarehousesDialog open={dialog === 'warehouses'} canManage={manage} onOpenChange={(open) => !open && setDialog(null)} />
    </div>
  );
}

function Levels({ warehouseId, lowOnly: initialLow }: { warehouseId?: string; lowOnly: boolean }) {
  const [search, setSearch] = useState('');
  const [lowOnly, setLowOnly] = useState(initialLow);
  const [page, setPage] = useState(1);
  const term = useDeferredValue(search.trim());
  const levels = useQuery({
    queryKey: queryKeys.erpStock({ warehouseId, search: term, lowOnly, page }),
    queryFn: ({ signal }) => api.erp.stock.levels(warehouseId, undefined, term || undefined, lowOnly, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <Card>
      <CardContent className='space-y-4'>
        <div className='flex flex-wrap gap-2'>
          <Input
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setPage(1);
            }}
            placeholder='搜索产品名称或编码'
            className='w-56'
          />
          <Select
            value={lowOnly ? 'low' : 'all'}
            onValueChange={(v) => {
              setLowOnly(v === 'low');
              setPage(1);
            }}
          >
            <SelectTrigger className='w-32'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value='all'>全部</SelectItem>
              <SelectItem value='low'>需要补货</SelectItem>
            </SelectContent>
          </Select>
        </div>
        {levels.isPending ? (
          <Skeleton className='h-40 w-full' />
        ) : levels.data?.items.length ? (
          <>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>产品</TableHead>
                  <TableHead>仓库</TableHead>
                  <TableHead className='text-right'>现有</TableHead>
                  <TableHead className='text-right'>已预留</TableHead>
                  <TableHead className='text-right'>可用</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {levels.data.items.map((l) => (
                  <TableRow key={`${l.productId}:${l.warehouseId}`}>
                    <TableCell className='max-w-xs'>
                      <Link href={`/erp/products/view?id=${l.productId}`} className='block truncate font-medium hover:underline'>
                        {l.productName}
                      </Link>
                      <span className='text-muted-foreground font-mono text-xs'>{l.sku}</span>
                    </TableCell>
                    <TableCell>{l.warehouseName}</TableCell>
                    <TableCell className='text-right tabular-nums'>{formatQuantity(l.onHand)}</TableCell>
                    <TableCell className='text-muted-foreground text-right tabular-nums'>{formatQuantity(l.reserved)}</TableCell>
                    <TableCell className='text-right font-medium tabular-nums'>{formatQuantity(l.available, l.unit)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={levels.data.total} onChange={setPage} />
          </>
        ) : (
          <EmptyState
            title={lowOnly ? '没有需要补货的产品' : '还没有库存'}
            description={lowOnly ? '可用数量都在补货提醒线以上。' : '用“调整库存”录入期初数量，或通过采购订单收货入库。'}
          />
        )}
      </CardContent>
    </Card>
  );
}

function Movements({ warehouseId, productId }: { warehouseId?: string; productId?: string }) {
  const [kind, setKind] = useState<ErpStockMovementKind | undefined>();
  const [page, setPage] = useState(1);
  const movements = useQuery({
    queryKey: queryKeys.erpMovements({ warehouseId, productId, kind, page }),
    queryFn: ({ signal }) => api.erp.stock.movements(productId, warehouseId, kind, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });
  const productName = productId ? movements.data?.items[0]?.productName : undefined;

  return (
    <Card>
      <CardContent className='space-y-4'>
        <div className='flex flex-wrap items-center gap-2'>
          <Select
            value={kind ?? ALL}
            onValueChange={(v) => {
              setKind(v === ALL ? undefined : (v as ErpStockMovementKind));
              setPage(1);
            }}
          >
            <SelectTrigger className='w-32'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>全部类型</SelectItem>
              {Object.entries(erpMovementKind).map(([key, label]) => (
                <SelectItem key={key} value={key}>
                  {label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {productId ? (
            <Link href='/erp/inventory' className='bg-muted inline-flex h-8 items-center gap-1 rounded-md px-3 text-sm'>
              只看：{productName ?? '所选产品'}
              <Icons.close className='size-3.5' />
            </Link>
          ) : null}
        </div>
        {movements.isPending ? (
          <Skeleton className='h-40 w-full' />
        ) : movements.data?.items.length ? (
          <>
            <MovementTable movements={movements.data.items} showProduct={!productId} />
            <Pager page={page} pageSize={PAGE_SIZE} total={movements.data.total} onChange={setPage} />
          </>
        ) : (
          <EmptyState title='没有流水' description='入库、出库、调整和调拨都会在这里留下记录。' />
        )}
      </CardContent>
    </Card>
  );
}

export default function InventoryPage() {
  return (
    <Suspense>
      <Inventory />
    </Suspense>
  );
}
