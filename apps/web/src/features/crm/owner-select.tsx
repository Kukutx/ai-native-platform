'use client';

import { useQuery } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const NOBODY = '__none__';

/**
 * 选择负责的成员。看不到成员名单（没有成员查看权限）时不显示，保存时沿用原负责人，新建时默认自己负责。
 */
export function OwnerSelect({ value, onChange }: { value?: string; onChange: (userId: string | undefined) => void }) {
  const { can } = useSession();
  const visible = can('platform.members.view');
  const members = useQuery({ queryKey: queryKeys.members, queryFn: ({ signal }) => api.members.list(signal), enabled: visible });
  if (!visible) {
    return null;
  }

  return (
    <div className='grid gap-2'>
      <Label>负责人</Label>
      <Select value={value ?? NOBODY} onValueChange={(v) => onChange(v === NOBODY ? undefined : v)}>
        <SelectTrigger className='w-full'>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={NOBODY}>暂无负责人</SelectItem>
          {members.data
            ?.filter((m) => m.status === 'active' || m.userId === value)
            .map((m) => (
              <SelectItem key={m.userId} value={m.userId}>
                {m.displayName}
              </SelectItem>
            ))}
        </SelectContent>
      </Select>
    </div>
  );
}
