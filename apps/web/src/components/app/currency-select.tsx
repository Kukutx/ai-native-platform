'use client';

import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { cn } from '@/lib/utils';

const CURRENCIES = ['CNY', 'USD', 'EUR', 'HKD', 'JPY', 'GBP'];

/** 常用货币；当前值不在列表里（例如智能体录入的其他货币）时也保留显示。 */
export function CurrencySelect({
  value,
  onChange,
  disabled,
  className,
}: {
  value: string;
  onChange: (currency: string) => void;
  disabled?: boolean;
  className?: string;
}) {
  const currencies = CURRENCIES.includes(value) ? CURRENCIES : [...CURRENCIES, value];
  return (
    <Select value={value} onValueChange={onChange} disabled={disabled}>
      <SelectTrigger className={cn('w-24', className)}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {currencies.map((c) => (
          <SelectItem key={c} value={c}>
            {c}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
