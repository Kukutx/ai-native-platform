import { Icons } from '@/components/icons';

import { cn } from '@/lib/utils';

function Spinner({ className, ...props }: React.ComponentProps<'svg'>) {
  return (
    <Icons.spinner
      role='status'
      aria-label='加载中'
      className={cn('size-4 animate-spin', className)}
      {...props}
    />
  );
}

export { Spinner };
