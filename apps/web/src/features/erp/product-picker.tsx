'use client';

import { useDeferredValue, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ErpProductDto } from '@ainative/api-client';
import { Input } from '@/components/ui/input';
import { api } from '@/lib/api';
import { formatQuantity } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

/**
 * 按名称或编码搜索在售产品，选中后交给调用方（例如加一行订单明细），输入框随即清空以便继续添加。
 */
export function ProductPicker({ onPick, placeholder }: { onPick: (product: ErpProductDto) => void; placeholder?: string }) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const term = useDeferredValue(search.trim());
  const results = useQuery({
    queryKey: queryKeys.erpProducts({ search: term, picker: true }),
    queryFn: async ({ signal }) => (await api.erp.products.list(term || undefined, undefined, undefined, undefined, 1, 8, signal)).items,
    enabled: open,
    placeholderData: keepPreviousData,
  });

  return (
    <div className='relative'>
      <Input
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        onFocus={() => setOpen(true)}
        // 延迟关闭，让点击结果先生效。
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        placeholder={placeholder ?? '搜索产品名称或编码'}
      />
      {open ? (
        <div className='bg-popover absolute z-50 mt-1 max-h-64 w-full overflow-y-auto rounded-md border p-1 shadow-md'>
          {results.data?.length ? (
            results.data.map((product) => (
              <button
                key={product.id}
                type='button'
                className='hover:bg-accent flex w-full items-center justify-between gap-3 rounded-sm px-2 py-1.5 text-left text-sm'
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  onPick(product);
                  setSearch('');
                }}
              >
                <span className='min-w-0'>
                  <span className='block truncate'>{product.name}</span>
                  <span className='text-muted-foreground text-xs'>{product.sku}</span>
                </span>
                <span className='text-muted-foreground shrink-0 text-xs'>
                  {product.trackInventory ? `可用 ${formatQuantity(product.available, product.unit)}` : '不管理库存'}
                </span>
              </button>
            ))
          ) : (
            <p className='text-muted-foreground px-2 py-1.5 text-sm'>{results.isFetching ? '搜索中…' : '没有找到在售的产品'}</p>
          )}
        </div>
      ) : null}
    </div>
  );
}
