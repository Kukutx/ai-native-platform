import type { ProjectProgressDto } from '@ainative/api-client';
import { cn } from '@/lib/utils';

/** 任务完成进度条，附「已完成/总数」；有阻塞或逾期时在后面提示。 */
export function ProgressBar({ progress, className, compact }: { progress: ProjectProgressDto; className?: string; compact?: boolean }) {
  return (
    <div className={cn('space-y-1', className)}>
      <div className='bg-muted h-1.5 overflow-hidden rounded-full'>
        <div className='bg-primary h-full rounded-full transition-[width]' style={{ width: `${progress.percent}%` }} />
      </div>
      <div className='text-muted-foreground flex flex-wrap gap-x-3 text-xs tabular-nums'>
        <span>
          {progress.total ? `${progress.done}/${progress.total} 已完成 · ${progress.percent}%` : '还没有任务'}
        </span>
        {!compact && progress.inProgress ? <span>进行中 {progress.inProgress}</span> : null}
        {progress.blocked ? <span className='text-destructive'>受阻 {progress.blocked}</span> : null}
        {progress.overdue ? <span className='text-amber-600 dark:text-amber-500'>逾期 {progress.overdue}</span> : null}
      </div>
    </div>
  );
}
