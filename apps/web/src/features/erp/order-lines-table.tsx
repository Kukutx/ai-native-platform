import Link from 'next/link';
import type { ErpOrderLineDto } from '@ainative/api-client';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatAmount, formatQuantity } from '@/lib/labels';

/** 订单明细（只读）。 */
export function OrderLinesTable({ lines, currency, total }: { lines: ErpOrderLineDto[]; currency: string; total: number }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>产品</TableHead>
          <TableHead className='text-right'>数量</TableHead>
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
            <TableCell className='text-right tabular-nums'>{formatAmount(line.unitPrice, currency)}</TableCell>
            <TableCell className='text-right tabular-nums'>{formatAmount(line.amount, currency)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
      <TableFooter>
        <TableRow>
          <TableCell colSpan={3}>合计</TableCell>
          <TableCell className='text-right font-semibold tabular-nums'>{formatAmount(total, currency)}</TableCell>
        </TableRow>
      </TableFooter>
    </Table>
  );
}
