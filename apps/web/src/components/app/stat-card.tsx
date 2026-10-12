import { Card, CardContent } from '@/components/ui/card';
import { cn } from '@/lib/utils';

/** 概览数字卡片：标签 + 主数字，可附一行说明。 */
export function StatCard({ label, value, hint, detail, tone }: { label: string; value: string; hint?: string; detail?: string; tone?: 'warning' }) {
  return (
    <Card className='py-4'>
      <CardContent className='space-y-1 px-4'>
        <p className='text-muted-foreground text-xs' title={hint}>
          {label}
        </p>
        <p className={cn('truncate text-xl font-semibold tabular-nums', tone === 'warning' && 'text-warning')} title={value}>
          {value}
        </p>
        {detail ? <p className='text-muted-foreground truncate text-xs'>{detail}</p> : null}
      </CardContent>
    </Card>
  );
}
