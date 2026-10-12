import Link from 'next/link';
import type { ErpStockMovementDto } from '@ainative/api-client';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { erpMovementKind, formatAmount, formatQuantity, formatTime } from '@/lib/labels';
import { cn } from '@/lib/utils';

/** 库存流水：每次入库、出库、调整、调拨、退货各一行，结存为该仓库变动后的现有数量，成本为这笔变动按单位成本计的金额。 */
export function MovementTable({ movements, showProduct }: { movements: ErpStockMovementDto[]; showProduct?: boolean }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>时间</TableHead>
          {showProduct ? <TableHead>产品</TableHead> : null}
          <TableHead>仓库</TableHead>
          <TableHead>类型</TableHead>
          <TableHead className='text-right'>数量</TableHead>
          <TableHead className='text-right'>结存</TableHead>
          <TableHead className='text-right'>成本</TableHead>
          <TableHead>说明</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {movements.map((m) => (
          <TableRow key={m.id}>
            <TableCell className='text-muted-foreground text-xs whitespace-nowrap'>{formatTime(m.createdAt)}</TableCell>
            {showProduct ? (
              <TableCell className='max-w-48'>
                <Link href={`/erp/products/view?id=${m.productId}`} className='block truncate hover:underline'>
                  {m.productName}
                </Link>
                <span className='text-muted-foreground font-mono text-xs'>{m.sku}</span>
              </TableCell>
            ) : null}
            <TableCell>{m.warehouseName}</TableCell>
            <TableCell>{erpMovementKind[m.kind]}</TableCell>
            <TableCell className={cn('text-right tabular-nums', m.quantity > 0 ? 'text-emerald-600 dark:text-emerald-500' : 'text-foreground')}>
              {m.quantity > 0 ? '+' : ''}
              {formatQuantity(m.quantity)}
            </TableCell>
            <TableCell className='text-right tabular-nums'>{formatQuantity(m.balanceAfter, m.unit)}</TableCell>
            <TableCell className='text-muted-foreground text-right tabular-nums' title={`单位成本 ${formatAmount(m.unitCost, m.currency)}`}>
              {formatAmount(m.costAmount, m.currency)}
            </TableCell>
            <TableCell className='text-muted-foreground max-w-64 text-xs'>
              {m.salesOrderId ? (
                <Link href={`/erp/sales-orders/view?id=${m.salesOrderId}`} className='text-foreground hover:underline'>
                  {m.salesOrderNumber}
                </Link>
              ) : null}
              {m.purchaseOrderId ? (
                <Link href={`/erp/purchase-orders/view?id=${m.purchaseOrderId}`} className='text-foreground hover:underline'>
                  {m.purchaseOrderNumber}
                </Link>
              ) : null}
              {m.note ? <span className='block truncate'>{m.note}</span> : null}
              <span className='block'>{m.actor.name ?? '系统'}</span>
            </TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}
