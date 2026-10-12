'use client';

import type { ErpOrderLineDto, ErpOrderLineRequest, ErpProductDto } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatAmount, formatQuantity } from '@/lib/labels';
import { ProductPicker } from './product-picker';
import { toNumber } from './utils';

export interface LineDraft {
  productId: string;
  sku: string;
  name: string;
  unit: string;
  /** 只有新选的产品才知道当前可用数量，用于提示。 */
  available?: number;
  quantity: string;
  unitPrice: string;
}

export function fromOrderLine(line: ErpOrderLineDto): LineDraft {
  return {
    productId: line.productId,
    sku: line.sku,
    name: line.productName,
    unit: line.unit,
    quantity: line.quantity.toString(),
    unitPrice: line.unitPrice.toString(),
  };
}

export function toLineRequests(lines: LineDraft[]): ErpOrderLineRequest[] {
  return lines.map((l) => ({ productId: l.productId, quantity: Number(l.quantity), unitPrice: toNumber(l.unitPrice) }));
}

function lineAmount(line: LineDraft) {
  const quantity = Number(line.quantity);
  const price = Number(line.unitPrice);
  return Number.isFinite(quantity) && Number.isFinite(price) ? Math.round(quantity * price * 100) / 100 : 0;
}

/**
 * 订单明细：搜索产品逐行添加，数量、单价可改。单价默认取产品的售价（销售）或采购价（采购），
 * 货币不同时留空，需要手填。同一产品再次添加时累加数量。
 */
export function OrderLinesEditor({
  lines,
  onChange,
  currency,
  priceOf,
  showAvailability,
}: {
  lines: LineDraft[];
  onChange: (lines: LineDraft[]) => void;
  currency: string;
  priceOf: (product: ErpProductDto) => number | undefined;
  showAvailability?: boolean;
}) {
  const update = (index: number, patch: Partial<LineDraft>) => onChange(lines.map((l, i) => (i === index ? { ...l, ...patch } : l)));
  const add = (product: ErpProductDto) => {
    const existing = lines.findIndex((l) => l.productId === product.id);
    if (existing >= 0) {
      update(existing, { quantity: String((Number(lines[existing].quantity) || 0) + 1) });
      return;
    }

    const price = product.currency === currency ? priceOf(product) : undefined;
    onChange([
      ...lines,
      {
        productId: product.id,
        sku: product.sku,
        name: product.name,
        unit: product.unit,
        available: product.trackInventory ? product.available : undefined,
        quantity: '1',
        unitPrice: price?.toString() ?? '',
      },
    ]);
  };
  const total = lines.reduce((sum, l) => sum + lineAmount(l), 0);

  return (
    <div className='grid gap-2'>
      {lines.length ? (
        <div className='rounded-lg border'>
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>产品</TableHead>
                <TableHead className='w-28'>数量</TableHead>
                <TableHead className='w-32'>单价</TableHead>
                <TableHead className='w-28 text-right'>金额</TableHead>
                <TableHead className='w-10' />
              </TableRow>
            </TableHeader>
            <TableBody>
              {lines.map((line, index) => {
                const short = showAvailability && line.available !== undefined && Number(line.quantity) > line.available;
                return (
                  <TableRow key={line.productId}>
                    <TableCell className='max-w-0'>
                      <p className='truncate font-medium'>{line.name}</p>
                      <p className={short ? 'text-destructive text-xs' : 'text-muted-foreground text-xs'}>
                        {line.sku}
                        {line.available !== undefined && showAvailability ? ` · 可用 ${formatQuantity(line.available, line.unit)}` : ''}
                      </p>
                    </TableCell>
                    <TableCell>
                      <Input
                        type='number'
                        min={0}
                        step='any'
                        required
                        value={line.quantity}
                        onChange={(e) => update(index, { quantity: e.target.value })}
                        className='h-8'
                        aria-label={`${line.name} 数量`}
                      />
                    </TableCell>
                    <TableCell>
                      <Input
                        type='number'
                        min={0}
                        step='any'
                        value={line.unitPrice}
                        onChange={(e) => update(index, { unitPrice: e.target.value })}
                        placeholder='按产品价格'
                        className='h-8'
                        aria-label={`${line.name} 单价`}
                      />
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{formatAmount(lineAmount(line), currency)}</TableCell>
                    <TableCell>
                      <Button
                        type='button'
                        size='icon'
                        variant='ghost'
                        className='size-7'
                        onClick={() => onChange(lines.filter((_, i) => i !== index))}
                        aria-label='删除这一行'
                      >
                        <Icons.close className='size-4' />
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
            <TableFooter>
              <TableRow>
                <TableCell colSpan={3}>合计</TableCell>
                <TableCell className='text-right tabular-nums'>{formatAmount(total, currency)}</TableCell>
                <TableCell />
              </TableRow>
            </TableFooter>
          </Table>
        </div>
      ) : null}
      <ProductPicker onPick={add} placeholder={lines.length ? '继续添加产品' : '搜索产品名称或编码，添加到订单'} />
    </div>
  );
}
