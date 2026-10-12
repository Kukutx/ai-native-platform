'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { Pager } from '@/components/app/pager';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ErpNav } from '@/features/erp/erp-nav';
import { ProductFormDialog } from '@/features/erp/product-form-dialog';
import { useSearchText, useUrlState } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { formatAmount, formatQuantity } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

const PAGE_SIZE = 50;
const ALL = '__all__';

export default function ErpProductsPage() {
  return (
    <Suspense>
      <ErpProducts />
    </Suspense>
  );
}

function ErpProducts() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [filters, setFilters] = useUrlState({ q: '', category: '', inactive: false, page: 1 });
  const { q: term, inactive: includeInactive, page } = filters;
  const category = filters.category || undefined;
  const [search, setSearch] = useSearchText(term, (q) => setFilters({ q, page: 1 }));
  const categories = useQuery({ queryKey: queryKeys.erpCategories, queryFn: ({ signal }) => api.erp.products.categories(signal) });
  const products = useQuery({
    queryKey: queryKeys.erpProducts({ search: term, category, includeInactive, page }),
    queryFn: ({ signal }) => api.erp.products.list(term || undefined, category, undefined, includeInactive, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });
  const filtered = !!term || !!category;

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='产品'
        description='卖出和采购的商品与服务。库存数量为全部仓库合计，可用 = 现有 − 已预留给订单的数量。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder='搜索名称或编码'
              aria-label='搜索产品'
              className='w-48'
            />
            {categories.data?.length ? (
              <Select
                value={category ?? ALL}
                onValueChange={(v) => setFilters({ category: v === ALL ? '' : v, page: 1 })}
              >
                <SelectTrigger className='w-32'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>全部分类</SelectItem>
                  {categories.data.map((c) => (
                    <SelectItem key={c} value={c}>
                      {c}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : null}
            <Select
              value={includeInactive ? 'all' : 'active'}
              onValueChange={(v) => setFilters({ inactive: v === 'all', page: 1 })}
            >
              <SelectTrigger className='w-28'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value='active'>在售</SelectItem>
                <SelectItem value='all'>含停售</SelectItem>
              </SelectContent>
            </Select>
            {can('erp.inventory') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建产品
              </Button>
            ) : null}
          </>
        }
      />
      <ErpNav />
      {products.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : products.isError && !products.data ? (
        <ErrorState query={products} />
      ) : products.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>编码</TableHead>
                  <TableHead>名称</TableHead>
                  <TableHead className='text-right'>售价</TableHead>
                  <TableHead className='text-right'>现有</TableHead>
                  <TableHead className='text-right'>已预留</TableHead>
                  <TableHead className='text-right'>可用</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {products.data.items.map((p) => (
                  <TableRow key={p.id} className={cn(!p.isActive && 'text-muted-foreground')}>
                    <TableCell className='font-mono text-xs'>{p.sku}</TableCell>
                    <TableCell className='max-w-xs'>
                      <Link href={`/erp/products/view?id=${p.id}`} className='font-medium hover:underline'>
                        {p.name}
                      </Link>
                      <div className='mt-1 flex flex-wrap gap-1'>
                        {p.category ? (
                          <Badge variant='outline' className='text-xs font-normal'>
                            {p.category}
                          </Badge>
                        ) : null}
                        {p.isActive ? null : (
                          <Badge variant='outline' className='text-xs font-normal'>
                            停售
                          </Badge>
                        )}
                      </div>
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{formatAmount(p.price, p.currency)}</TableCell>
                    {p.trackInventory ? (
                      <>
                        <TableCell className='text-right tabular-nums'>{formatQuantity(p.onHand)}</TableCell>
                        <TableCell className='text-muted-foreground text-right tabular-nums'>{formatQuantity(p.reserved)}</TableCell>
                        <TableCell className={cn('text-right font-medium tabular-nums', p.lowStock && 'text-warning')}>
                          {formatQuantity(p.available, p.unit)}
                        </TableCell>
                      </>
                    ) : (
                      <TableCell colSpan={3} className='text-muted-foreground text-right text-xs'>
                        不管理库存
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={products.data.total} onChange={(next) => setFilters({ page: next })} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState
          title={filtered ? '没有找到产品' : '还没有产品'}
          description={filtered ? '换个关键字或分类试试。' : '先建好产品，再录入期初库存，就可以开销售和采购订单了。'}
        />
      )}
      <ProductFormDialog open={creating} onOpenChange={setCreating} onSaved={(p) => router.push(`/erp/products/view?id=${p.id}`)} />
    </div>
  );
}
