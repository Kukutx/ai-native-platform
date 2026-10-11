'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { ErpProductDto } from '@ainative/api-client';
import { toast } from 'sonner';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { api } from '@/lib/api';
import { formatQuantity } from '@/lib/labels';
import { ProductPicker } from './product-picker';
import { WarehouseSelect } from './warehouse-select';

type PickedProduct = Pick<ErpProductDto, 'id' | 'sku' | 'name' | 'unit'>;

function ProductField({ product, onChange }: { product: PickedProduct | null; onChange: (product: PickedProduct | null) => void }) {
  if (product) {
    return (
      <div className='flex h-9 items-center justify-between rounded-md border px-3 text-sm'>
        <span className='truncate'>
          {product.name} <span className='text-muted-foreground'>· {product.sku}</span>
        </span>
        <Button type='button' size='icon' variant='ghost' className='size-6' onClick={() => onChange(null)} aria-label='清除'>
          <Icons.close className='size-4' />
        </Button>
      </div>
    );
  }

  return <ProductPicker onPick={onChange} />;
}

/**
 * 调整库存：按增减数量（期初、报损、赠品等），或按盘点结果直接改成实际数量。每次调整都会留下库存流水。
 */
export function AdjustStockDialog({
  open,
  product: initialProduct,
  warehouseId: initialWarehouse,
  onOpenChange,
}: {
  open: boolean;
  product?: PickedProduct | null;
  warehouseId?: string;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-md'>
        {open ? <AdjustForm product={initialProduct ?? null} warehouseId={initialWarehouse} onClose={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function AdjustForm({ product: initialProduct, warehouseId: initialWarehouse, onClose }: { product: PickedProduct | null; warehouseId?: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [mode, setMode] = useState<'delta' | 'count'>('delta');
  const [product, setProduct] = useState(initialProduct);
  const [warehouseId, setWarehouseId] = useState(initialWarehouse);
  const [quantity, setQuantity] = useState('');
  const [note, setNote] = useState('');
  const save = useMutation({
    mutationFn: () =>
      api.erp.stock.adjust({
        productId: product?.id ?? '',
        warehouseId,
        quantity: mode === 'delta' ? Number(quantity) : undefined,
        countedQuantity: mode === 'count' ? Number(quantity) : undefined,
        note,
      }),
    onSuccess: async (level) => {
      toast.success(`${level.warehouseName} 现有 ${formatQuantity(level.onHand, level.unit)}`);
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      onClose();
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>调整库存</DialogTitle>
        <DialogDescription>录入期初、报损或盘点结果。采购入库和销售出库由订单自动完成，不需要在这里调整。</DialogDescription>
      </DialogHeader>
      <Tabs value={mode} onValueChange={(v) => setMode(v as 'delta' | 'count')}>
        <TabsList className='w-full'>
          <TabsTrigger value='delta'>增减数量</TabsTrigger>
          <TabsTrigger value='count'>盘点</TabsTrigger>
        </TabsList>
      </Tabs>
      <div className='grid gap-2'>
        <Label>产品</Label>
        <ProductField product={product} onChange={setProduct} />
      </div>
      <div className='grid gap-2'>
        <Label>仓库</Label>
        <WarehouseSelect value={warehouseId} onChange={setWarehouseId} allowDefault />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='adjust-quantity'>{mode === 'delta' ? '增减数量（减少填负数）' : '实际数量'}</Label>
        <Input
          id='adjust-quantity'
          type='number'
          step='any'
          min={mode === 'count' ? 0 : undefined}
          required
          value={quantity}
          onChange={(e) => setQuantity(e.target.value)}
          placeholder={mode === 'delta' ? '例如 100 或 -2' : '盘点后的实际数量'}
        />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='adjust-note'>原因</Label>
        <Input id='adjust-note' value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} placeholder='例如：期初录入、破损报废' />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || !product}>
          确认调整
        </Button>
      </DialogFooter>
    </form>
  );
}

/** 在两个仓库之间调拨：调出仓库的可用数量要够（已预留给订单的不能调走）。 */
export function TransferStockDialog({
  open,
  product: initialProduct,
  fromWarehouseId,
  onOpenChange,
}: {
  open: boolean;
  product?: PickedProduct | null;
  fromWarehouseId?: string;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-md'>
        {open ? <TransferForm product={initialProduct ?? null} fromWarehouseId={fromWarehouseId} onClose={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function TransferForm({ product: initialProduct, fromWarehouseId, onClose }: { product: PickedProduct | null; fromWarehouseId?: string; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [product, setProduct] = useState(initialProduct);
  const [from, setFrom] = useState(fromWarehouseId);
  const [to, setTo] = useState<string | undefined>();
  const [quantity, setQuantity] = useState('');
  const [note, setNote] = useState('');
  const save = useMutation({
    mutationFn: () => api.erp.stock.transfer({ productId: product?.id ?? '', fromWarehouseId: from ?? '', toWarehouseId: to ?? '', quantity: Number(quantity), note }),
    onSuccess: async () => {
      toast.success('已调拨');
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      onClose();
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>库存调拨</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label>产品</Label>
        <ProductField product={product} onChange={setProduct} />
      </div>
      <div className='grid grid-cols-2 gap-4'>
        <div className='grid gap-2'>
          <Label>调出仓库</Label>
          <WarehouseSelect value={from} onChange={setFrom} />
        </div>
        <div className='grid gap-2'>
          <Label>调入仓库</Label>
          <WarehouseSelect value={to} onChange={setTo} />
        </div>
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='transfer-quantity'>数量</Label>
        <Input id='transfer-quantity' type='number' step='any' min={0} required value={quantity} onChange={(e) => setQuantity(e.target.value)} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='transfer-note'>备注</Label>
        <Input id='transfer-note' value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
      </div>
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || !product || !from || !to || from === to}>
          确认调拨
        </Button>
      </DialogFooter>
    </form>
  );
}
