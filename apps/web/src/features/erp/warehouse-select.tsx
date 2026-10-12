'use client';

import { useQuery } from '@tanstack/react-query';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { catalogQueries } from '@/lib/catalogs';
import { cn } from '@/lib/utils';

const DEFAULT = '__default__';

/** 选择仓库；allowDefault 时多一个"默认仓库"选项（值为 undefined，由后端决定）。 */
export function WarehouseSelect({
  value,
  onChange,
  allowDefault,
  allLabel,
  className,
}: {
  value: string | undefined;
  onChange: (warehouseId: string | undefined) => void;
  allowDefault?: boolean;
  /** 用作筛选时的"全部仓库"选项文案。 */
  allLabel?: string;
  className?: string;
}) {
  const warehouses = useQuery(catalogQueries.erpWarehouses());
  const empty = allowDefault || allLabel;
  return (
    <Select value={value ?? (empty ? DEFAULT : '')} onValueChange={(v) => onChange(v === DEFAULT ? undefined : v)}>
      <SelectTrigger className={cn('w-full', className)}>
        <SelectValue placeholder='选择仓库' />
      </SelectTrigger>
      <SelectContent>
        {empty ? <SelectItem value={DEFAULT}>{allLabel ?? '默认仓库'}</SelectItem> : null}
        {warehouses.data?.map((w) => (
          <SelectItem key={w.id} value={w.id}>
            {w.name}
            {w.isDefault ? <span className='text-muted-foreground'>（默认）</span> : null}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
