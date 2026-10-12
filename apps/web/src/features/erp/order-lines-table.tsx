import Link from 'next/link';
import type { ErpOrderLineDto } from '@ainative/api-client';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatAmount, formatQuantity } from '@/lib/labels';

/**
 * 订单明细（只读）。deliveredLabel 有值时（例如“已发”“已收”）多显示交付进度一列，有退货时再加“已退”一列。
 */
export function OrderLinesTable({
  lines,
  currency,
  total,
  deliveredLabel,
}: {
  lines: ErpOrderLineDto[];
  currency: string;
  total: number;
  deliveredLabel?: string;
}) {
  const showReturned = !!deliveredLabel && lines.some((l) => l.returned > 0);
  const extra = (deliveredLabel ? 1 : 0) + (showReturned ? 1 : 0);

  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>产品</TableHead>
          <TableHead className='text-right'>数量</TableHead>
          {deliveredLabel ? <TableHead className='text-right'>{deliveredLabel}</TableHead> : null}
          {showReturned ? <TableHead className='text-right'>已退</TableHead> : null}
          <TableHead className='text-right'>单价</TableHead>
          <TableHead className='text-right'>金额</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {lines.map((line) => (
          <TableRow key={line.productId}>
            <TableCell className='max-w-xs'>
              <Link href={`/erp/products/view?id=${line.productId}`} className='block truncate font-medium hover:underline'>
                {line.productName}
              </Link>
              <span className='text-muted-foreground font-mono text-xs'>{line.sku}</span>
            </TableCell>
            <TableCell className='text-right tabular-nums'>{formatQuantity(line.quantity, line.unit)}</TableCell>
            {deliveredLabel ? (
              <TableCell className={line.delivered < line.quantity ? 'text-right text-amber-600 tabular-nums dark:text-amber-500' : 'text-right tabular-nums'}>
                {formatQuantity(line.delivered)}
              </TableCell>
            ) : null}
            {showReturned ? <TableCell className='text-muted-foreground text-right tabular-nums'>{line.returned ? formatQuantity(line.returned) : '—'}</TableCell> : null}
            <TableCell className='text-right tabular-nums'>{formatAmount(line.unitPrice, currency)}</TableCell>
            <TableCell className='text-right tabular-nums'>{formatAmount(line.amount, currency)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
      <TableFooter>
        <TableRow>
          <TableCell colSpan={3 + extra}>合计</TableCell>
          <TableCell className='text-right font-semibold tabular-nums'>{formatAmount(total, currency)}</TableCell>
        </TableRow>
      </TableFooter>
    </Table>
  );
}
