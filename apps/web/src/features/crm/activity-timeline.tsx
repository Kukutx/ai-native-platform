'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import type { CrmActivityDto, CrmActivityKind } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { crmActivityKind, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 30;
const LOGGABLE: CrmActivityKind[] = ['note', 'call', 'meeting', 'email'];

const kindIcon: Record<CrmActivityKind, React.ComponentType<{ className?: string }>> = {
  note: Icons.post,
  call: Icons.phone,
  meeting: Icons.teams,
  email: Icons.mail,
  stage_change: Icons.arrowRight,
};

export interface TimelineScope {
  companyId?: string;
  contactId?: string;
  dealId?: string;
}

/**
 * 跟进记录时间线与记录框。按客户查看时包含名下联系人和商机的往来。
 */
export function ActivityTimeline({ scope }: { scope: TimelineScope }) {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<CrmActivityKind>('note');
  const [content, setContent] = useState('');
  const activities = useInfiniteQuery({
    queryKey: queryKeys.crmActivities(scope),
    queryFn: ({ pageParam, signal }) => api.crm.activities.list(scope.companyId, scope.contactId, scope.dealId, pageParam, PAGE_SIZE, signal),
    initialPageParam: 1,
    getNextPageParam: (last, pages) => (pages.length * PAGE_SIZE < last.total ? pages.length + 1 : undefined),
  });
  const log = useMutation({
    mutationFn: () => api.crm.activities.log({ kind, content: content.trim(), ...scope }),
    onSuccess: async () => {
      setContent('');
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => api.crm.activities.delete(id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['crm'] }),
  });
  const manage = can('crm.manage');
  const items = activities.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <Card>
      <CardHeader>
        <CardTitle>跟进记录</CardTitle>
      </CardHeader>
      <CardContent className='space-y-5'>
        {manage ? (
          <form
            className='space-y-2'
            onSubmit={(event) => {
              event.preventDefault();
              if (content.trim()) {
                log.mutate();
              }
            }}
          >
            <Textarea value={content} onChange={(e) => setContent(e.target.value)} rows={3} placeholder='沟通了什么、对方反馈、下一步计划' />
            <div className='flex items-center gap-2'>
              <Select value={kind} onValueChange={(v) => setKind(v as CrmActivityKind)}>
                <SelectTrigger className='w-28' size='sm'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {LOGGABLE.map((k) => (
                    <SelectItem key={k} value={k}>
                      {crmActivityKind[k]}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <Button type='submit' size='sm' disabled={log.isPending || !content.trim()}>
                记录
              </Button>
            </div>
          </form>
        ) : null}
        {activities.isPending ? (
          <p className='text-muted-foreground text-sm'>加载中…</p>
        ) : items.length ? (
          <ol className='relative space-y-5 border-l pl-6'>
            {items.map((activity) => (
              <ActivityItem
                key={activity.id}
                activity={activity}
                scope={scope}
                onDelete={manage && activity.kind !== 'stage_change' ? () => confirm('删除这条跟进记录？') && remove.mutate(activity.id) : undefined}
              />
            ))}
          </ol>
        ) : (
          <p className='text-muted-foreground text-sm'>还没有跟进记录。</p>
        )}
        {activities.hasNextPage ? (
          <Button variant='outline' size='sm' disabled={activities.isFetchingNextPage} onClick={() => void activities.fetchNextPage()}>
            加载更早的记录
          </Button>
        ) : null}
      </CardContent>
    </Card>
  );
}

function ActivityItem({ activity, scope, onDelete }: { activity: CrmActivityDto; scope: TimelineScope; onDelete?: () => void }) {
  const Icon = kindIcon[activity.kind];
  // 当前页面本身就是的那个对象不再重复链接。
  const links = [
    activity.dealId && activity.dealId !== scope.dealId ? { href: `/crm/deals/view?id=${activity.dealId}`, label: activity.dealTitle ?? '商机' } : null,
    activity.contactId && activity.contactId !== scope.contactId ? { href: `/crm/contacts/view?id=${activity.contactId}`, label: activity.contactName ?? '联系人' } : null,
    activity.companyId && activity.companyId !== scope.companyId
      ? { href: `/crm/companies/view?id=${activity.companyId}`, label: activity.companyName ?? '客户' }
      : null,
  ].filter((link) => link !== null);

  return (
    <li className='group relative'>
      <span className='bg-background absolute top-0.5 -left-[2.1rem] flex size-6 items-center justify-center rounded-full border'>
        <Icon className='text-muted-foreground size-3.5' />
      </span>
      <div className='flex flex-wrap items-center gap-x-2 gap-y-1 text-sm'>
        <span className='font-medium'>{activity.author.name ?? '系统'}</span>
        {activity.author.kind === 'agent' ? <span className='text-muted-foreground text-xs'>智能体</span> : null}
        <span className='text-muted-foreground text-xs'>
          {crmActivityKind[activity.kind]} · {formatTime(activity.occurredAt)}
        </span>
        {links.map((link) => (
          <Link key={link.href} href={link.href} className='text-muted-foreground text-xs underline-offset-2 hover:underline'>
            {link.label}
          </Link>
        ))}
        {onDelete ? (
          <Button
            size='icon'
            variant='ghost'
            className='ml-auto size-6 opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100'
            onClick={onDelete}
            aria-label='删除'
          >
            <Icons.trash className='size-3.5' />
          </Button>
        ) : null}
      </div>
      <p className={activity.kind === 'stage_change' ? 'text-muted-foreground mt-1 text-sm' : 'mt-1 text-sm whitespace-pre-wrap'}>{activity.content}</p>
    </li>
  );
}
