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

/** 模型调用平台动作的可视化：动作名 + 结果状态。 */
export function ToolCallChip({ actionKey, status }: { actionKey: string; status?: string }) {
  return (
    <Badge variant='outline' className='gap-1 font-mono text-[11px]'>
      <Icons.code className='size-3' />
      {actionKey}
      {status ? <span className='text-muted-foreground font-sans'>· {statusText[status] ?? status}</span> : null}
    </Badge>
  );
}
