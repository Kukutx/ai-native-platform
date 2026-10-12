'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ErpProductDto } from '@ainative/api-client';
import { CurrencySelect } from '@/components/app/currency-select';
import { EditConflictAlert, isEditConflict, ReloadableForm, useEditVersion } from '@/components/app/edit-conflict';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { toNumber } from './utils';

/** product 为 null 时新建，否则编辑。 */
export function ProductFormDialog({
  open,
  product,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  product?: ErpProductDto | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (product: ErpProductDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-xl'>
        {open ? (
          <ReloadableForm entity={product ?? null}>
            {(current, reload, key) => <ProductForm key={key} product={current} onReload={reload} onClose={() => onOpenChange(false)} onSaved={onSaved} />}
          </ReloadableForm>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function ProductForm({
  product,
  onReload,
  onClose,
  onSaved,
}: {
  product: ErpProductDto | null;
  onReload: (latest: ErpProductDto) => void;
  onClose: () => void;
  onSaved?: (product: ErpProductDto) => void;
}) {
  const queryClient = useQueryClient();
  const edit = useEditVersion(product, async () => {
    const id = product!.id;
    const detail = await queryClient.fetchQuery({ queryKey: queryKeys.erpProduct(id), queryFn: ({ signal }) => api.erp.products.get(id, signal), staleTime: 0 });
    return detail.product;
  });
  const categories = useQuery({ queryKey: queryKeys.erpCategories, queryFn: ({ signal }) => api.erp.products.categories(signal) });
  const [form, setForm] = useState({
    sku: product?.sku ?? '',
    name: product?.name ?? '',
    unit: product?.unit ?? '件',
    category: product?.category ?? '',
    description: product?.description ?? '',
    price: product?.price?.toString() ?? '',
    cost: product?.cost?.toString() ?? '',
    reorderPoint: product?.reorderPoint?.toString() ?? '',
  });
  const [currency, setCurrency] = useState(product?.currency ?? 'CNY');
  const [trackInventory, setTrackInventory] = useState(product?.trackInventory ?? true);
  const [isActive, setIsActive] = useState(product?.isActive ?? true);
  const save = useMutation({
    mutationFn: () => {
      const request = {
        ...form,
        price: toNumber(form.price),
        cost: toNumber(form.cost),
        reorderPoint: trackInventory ? toNumber(form.reorderPoint) : undefined,
        currency,
        trackInventory,
        isActive,
        expectedUpdatedAt: edit.expected(),
      };
      return product ? api.erp.products.update(product.id, request) : api.erp.products.create(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['erp'] });
      onSaved?.(saved);
      onClose();
    },
    meta: { handledError: isEditConflict },
    // 冲突时保留表单里的修改，由用户选择载入最新或仍然覆盖；页面上的数据照常刷新。
    onError: (error) => {
      edit.onError(error);
      void queryClient.invalidateQueries({ queryKey: ['erp'] });
    },
  });
  const field = (key: keyof typeof form) => ({
    value: form[key],
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm({ ...form, [key]: e.target.value }),
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
        <DialogTitle>{product ? '编辑产品' : '新建产品'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-4 sm:grid-cols-3'>
        <div className='grid gap-2'>
          <Label htmlFor='product-sku'>编码</Label>
          <Input id='product-sku' {...field('sku')} required maxLength={64} placeholder='例如 W-100' className='uppercase' />
        </div>
        <div className='grid gap-2 sm:col-span-2'>
          <Label htmlFor='product-name'>名称</Label>
          <Input id='product-name' {...field('name')} required maxLength={200} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='product-unit'>单位</Label>
          <Input id='product-unit' {...field('unit')} required maxLength={20} placeholder='件、箱、次' />
        </div>
        <div className='grid gap-2 sm:col-span-2'>
          <Label htmlFor='product-category'>分类</Label>
          <Input id='product-category' {...field('category')} maxLength={50} list='product-categories' />
          <datalist id='product-categories'>
            {categories.data?.map((c) => (
              <option key={c} value={c} />
            ))}
          </datalist>
        </div>
      </div>
      <div className='grid gap-4 sm:grid-cols-3'>
        <div className='grid gap-2'>
          <Label htmlFor='product-price'>售价</Label>
          <Input id='product-price' type='number' min={0} step='0.0001' {...field('price')} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='product-cost'>采购价</Label>
          <Input id='product-cost' type='number' min={0} step='0.0001' {...field('cost')} />
        </div>
        <div className='grid gap-2'>
          <Label>货币</Label>
          <CurrencySelect value={currency} onChange={setCurrency} className='w-full' />
        </div>
      </div>
      <div className='flex flex-wrap items-center gap-x-6 gap-y-3 rounded-lg border p-3'>
        <label className='flex items-center gap-2 text-sm'>
          <Switch checked={trackInventory} onCheckedChange={setTrackInventory} />
          管理库存
        </label>
        {trackInventory ? (
          <div className='flex items-center gap-2'>
            <Label htmlFor='product-reorder' className='shrink-0 font-normal'>
              补货提醒线
            </Label>
            <Input id='product-reorder' type='number' min={0} step='0.0001' {...field('reorderPoint')} className='h-8 w-28' placeholder='不提醒' />
          </div>
        ) : (
          <p className='text-muted-foreground text-xs'>服务、虚拟商品等不占库存，下单和发货时不检查数量。</p>
        )}
        {product ? (
          <label className='ml-auto flex items-center gap-2 text-sm'>
            <Switch checked={isActive} onCheckedChange={setIsActive} />
            在售
          </label>
        ) : null}
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='product-description'>说明</Label>
        <Textarea id='product-description' {...field('description')} rows={3} placeholder='规格、型号、注意事项等' />
      </div>
      <EditConflictAlert edit={edit} subject='产品' onLoadLatest={onReload} onOverwrite={() => save.mutate()} disabled={save.isPending} />
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          保存
        </Button>
      </DialogFooter>
    </form>
  );
}
