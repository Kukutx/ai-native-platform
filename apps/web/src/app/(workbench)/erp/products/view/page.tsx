'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { InfoRow } from '@/features/crm/info-row';
import { MovementTable } from '@/features/erp/movement-table';
import { ProductFormDialog } from '@/features/erp/product-form-dialog';
import { AdjustStockDialog, TransferStockDialog } from '@/features/erp/stock-dialogs';
import { api } from '@/lib/api';
import { formatAmount, formatQuantity, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function ProductDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [adjusting, setAdjusting] = useState<{ warehouseId?: string } | null>(null);
  const [transferring, setTransferring] = useState<{ warehouseId?: string } | null>(null);
  const detail = useQuery({ queryKey: queryKeys.erpProduct(id), queryFn: ({ signal }) => api.erp.products.get(id, signal), enabled: !!id });
  const remove = useMutation({
    mutationFn: () => api.erp.products.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      router.replace('/erp/products');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const { product, levels, movements } = detail.data;
  const manage = can('erp.inventory');

  return (
    <div className='space-y-6 p-6'>
      <Link href='/erp/products' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        产品
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <div className='flex flex-wrap items-center gap-2'>
            <h1 className='text-2xl font-semibold tracking-tight'>{product.name}</h1>
            {product.isActive ? null : <Badge variant='outline'>停售</Badge>}
            {product.lowStock ? <Badge variant='destructive'>需要补货</Badge> : null}
          </div>
          <p className='text-muted-foreground font-mono text-sm'>{product.sku}</p>
        </div>
        {manage ? (
          <div className='flex flex-wrap gap-2'>
            {product.trackInventory ? (
              <>
                <Button variant='outline' onClick={() => setAdjusting({})}>
                  <Icons.adjustments />
                  调整库存
                </Button>
                <Button variant='outline' onClick={() => setTransferring({})}>
                  <Icons.arrowRight />
                  调拨
                </Button>
              </>
            ) : null}
            <Button variant='outline' onClick={() => setEditing(true)}>
              <Icons.edit />
              编辑
            </Button>
            <Button
              variant='ghost'
              disabled={remove.isPending}
              onClick={() => confirm(`删除产品「${product.name}」？用过的产品不能删除，可以改为停售。`) && remove.mutate()}
            >
              <Icons.trash />
              删除
            </Button>
          </div>
        ) : null}
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          {product.trackInventory ? (
            <Card>
              <CardHeader>
                <CardTitle>
                  库存 <span className='text-muted-foreground font-normal'>可用 {formatQuantity(product.available, product.unit)}</span>
                </CardTitle>
              </CardHeader>
              <CardContent>
                {levels.length ? (
                  <Table>
                    <TableHeader>
                      <TableRow>
                        <TableHead>仓库</TableHead>
                        <TableHead className='text-right'>现有</TableHead>
                        <TableHead className='text-right'>已预留</TableHead>
                        <TableHead className='text-right'>可用</TableHead>
                        <TableHead className='text-right'>平均成本</TableHead>
                        <TableHead className='text-right'>库存金额</TableHead>
                        {manage ? <TableHead className='w-24' /> : null}
                      </TableRow>
                    </TableHeader>
                    <TableBody>
                      {levels.map((l) => (
                        <TableRow key={l.warehouseId}>
                          <TableCell>{l.warehouseName}</TableCell>
                          <TableCell className='text-right tabular-nums'>{formatQuantity(l.onHand)}</TableCell>
                          <TableCell className='text-muted-foreground text-right tabular-nums'>{formatQuantity(l.reserved)}</TableCell>
                          <TableCell className='text-right font-medium tabular-nums'>{formatQuantity(l.available, l.unit)}</TableCell>
                          <TableCell className='text-muted-foreground text-right tabular-nums'>{formatAmount(l.averageCost, l.currency)}</TableCell>
                          <TableCell className='text-right tabular-nums'>{formatAmount(l.stockValue, l.currency)}</TableCell>
                          {manage ? (
                            <TableCell className='text-right'>
                              <Button size='sm' variant='ghost' onClick={() => setAdjusting({ warehouseId: l.warehouseId })}>
                                盘点
                              </Button>
                            </TableCell>
                          ) : null}
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                ) : (
                  <p className='text-muted-foreground text-sm'>还没有库存。用“调整库存”录入期初数量，或通过采购订单收货入库。</p>
                )}
              </CardContent>
            </Card>
          ) : null}
          <Card>
            <CardHeader>
              <CardTitle>库存流水</CardTitle>
            </CardHeader>
            <CardContent>
              {movements.length ? (
                <>
                  <MovementTable movements={movements} />
                  <Link href={`/erp/inventory?productId=${product.id}`} className='text-muted-foreground hover:text-foreground mt-3 inline-block text-xs'>
                    查看全部流水
                  </Link>
                </>
              ) : (
                <p className='text-muted-foreground text-sm'>还没有出入库记录。</p>
              )}
            </CardContent>
          </Card>
        </div>
        <Card className='h-fit'>
          <CardHeader>
            <CardTitle>资料</CardTitle>
          </CardHeader>
          <CardContent className='space-y-3 text-sm'>
            <InfoRow icon={Icons.tag} label='分类'>
              {product.category || null}
            </InfoRow>
            <InfoRow icon={Icons.product} label='单位'>
              {product.unit}
            </InfoRow>
            <InfoRow icon={Icons.creditCard} label='售价'>
              <span className='tabular-nums'>{formatAmount(product.price, product.currency)}</span>
            </InfoRow>
            <InfoRow icon={Icons.billing} label='采购价'>
              <span className='tabular-nums'>{formatAmount(product.cost, product.currency)}</span>
            </InfoRow>
            {product.trackInventory ? (
              <>
                <InfoRow icon={Icons.chartLine} label='平均成本'>
                  {product.averageCost !== undefined ? <span className='tabular-nums'>{formatAmount(product.averageCost, product.currency)}</span> : null}
                </InfoRow>
                <InfoRow icon={Icons.receipt} label='库存金额'>
                  <span className='tabular-nums'>{formatAmount(product.stockValue, product.currency)}</span>
                </InfoRow>
              </>
            ) : null}
            <InfoRow icon={Icons.warning} label='提醒线'>
              {product.trackInventory ? (product.reorderPoint !== undefined ? formatQuantity(product.reorderPoint, product.unit) : '不提醒') : '不管理库存'}
            </InfoRow>
            {product.description ? <p className='border-t pt-3 whitespace-pre-wrap'>{product.description}</p> : null}
            <p className='text-muted-foreground border-t pt-3 text-xs'>更新于 {formatTime(product.updatedAt)}</p>
          </CardContent>
        </Card>
      </div>
      <ProductFormDialog open={editing} product={product} onOpenChange={setEditing} />
      <AdjustStockDialog open={!!adjusting} product={product} warehouseId={adjusting?.warehouseId} onOpenChange={(open) => !open && setAdjusting(null)} />
      <TransferStockDialog open={!!transferring} product={product} onOpenChange={(open) => !open && setTransferring(null)} />
    </div>
  );
}

export default function ProductDetailPage() {
  return (
    <Suspense>
      <ProductDetail />
    </Suspense>
  );
}
