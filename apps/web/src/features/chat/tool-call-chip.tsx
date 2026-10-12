'use client';

import Link from 'next/link';
import { RawKeyTooltip, useActionTitles } from '@/components/app/raw-key';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';

const statusText: Record<string, string> = {
  succeeded: '已完成',
  pending_approval: '待审批',
  failed: '失败',
  forbidden: '无权限',
  not_found: '不存在',
  invalid_input: '参数有误',
};

/** 模型调用平台动作的可视化：动作名称 + 结果状态，悬停显示动作键；等待审批时点击进入审批页。 */
export function ToolCallChip({ actionKey, status }: { actionKey: string; status?: string }) {
  const { can } = useSession();
  const title = useActionTitles()(actionKey);
  const pending = status === 'pending_approval';
  const content = (
    <>
      <Icons.code className='size-3' />
      {title ?? <span className='font-mono'>{actionKey}</span>}
      {status ? <span className={pending ? 'text-warning' : 'text-muted-foreground'}>· {statusText[status] ?? status}</span> : null}
    </>
  );

  return (
    <RawKeyTooltip value={actionKey}>
      {pending && can('approvals.view') ? (
        <Badge variant='outline' className='gap-1 text-[11px]' asChild>
          <Link href='/approvals'>
            {content}
            <Icons.chevronRight className='size-3' />
          </Link>
        </Badge>
      ) : (
        <Badge variant='outline' className='gap-1 text-[11px]' tabIndex={0}>
          {content}
        </Badge>
      )}
    </RawKeyTooltip>
  );
}
