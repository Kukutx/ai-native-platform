'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { ActivityTimeline } from '@/features/crm/activity-timeline';
import { ContactFormDialog } from '@/features/crm/contact-form-dialog';
import { DealFormDialog } from '@/features/crm/deal-form-dialog';
import { InfoRow } from '@/features/crm/info-row';
import { api } from '@/lib/api';
import { crmDealStatus, formatAmount, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function ContactDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [addingDeal, setAddingDeal] = useState(false);
  const detail = useQuery({ queryKey: queryKeys.crmContact(id), queryFn: ({ signal }) => api.crm.contacts.get(id, signal), enabled: !!id });
  const remove = useMutation({
    mutationFn: () => api.crm.contacts.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      router.replace('/crm/contacts');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const { contact, deals } = detail.data;
  const manage = can('crm.manage');

  return (
    <div className='space-y-6 p-6'>
      <Link href='/crm/contacts' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        联系人
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <h1 className='text-2xl font-semibold tracking-tight'>{contact.name}</h1>
          <div className='text-muted-foreground flex flex-wrap items-center gap-2 text-sm'>
            {contact.title ? <span>{contact.title}</span> : null}
            {contact.companyId ? (
              <Link href={`/crm/companies/view?id=${contact.companyId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.building className='size-4' />
                {contact.companyName}
              </Link>
            ) : null}
            {contact.tags.map((tag) => (
              <Badge key={tag} variant='outline' className='font-normal'>
                {tag}
              </Badge>
            ))}
          </div>
        </div>
        {manage ? (
          <div className='flex gap-2'>
            <Button variant='outline' onClick={() => setEditing(true)}>
              <Icons.edit />
              编辑
            </Button>
            <Button variant='ghost' disabled={remove.isPending} onClick={() => confirm(`删除联系人「${contact.name}」？相关商机会保留。`) && remove.mutate()}>
              <Icons.trash />
              删除
            </Button>
          </div>
        ) : null}
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          <ActivityTimeline scope={{ contactId: contact.id }} />
        </div>
        <div className='space-y-6'>
          <Card>
            <CardHeader>
              <CardTitle>联系方式</CardTitle>
            </CardHeader>
            <CardContent className='space-y-3 text-sm'>
              <InfoRow icon={Icons.phone} label='电话'>
                {contact.phone ? <a href={`tel:${contact.phone}`} className='hover:underline'>{contact.phone}</a> : null}
              </InfoRow>
              <InfoRow icon={Icons.mail} label='邮箱'>
                {contact.email ? <a href={`mailto:${contact.email}`} className='break-all hover:underline'>{contact.email}</a> : null}
              </InfoRow>
              <InfoRow icon={Icons.user} label='负责人'>
                {contact.owner?.name ?? null}
              </InfoRow>
              {contact.description ? <p className='text-muted-foreground border-t pt-3 whitespace-pre-wrap'>{contact.description}</p> : null}
              <p className='text-muted-foreground border-t pt-3 text-xs'>
                {contact.createdBy.name ?? '系统'} 创建于 {formatTime(contact.createdAt)}
              </p>
            </CardContent>
          </Card>
          <Card>
            <CardHeader className='flex flex-row items-center justify-between'>
              <CardTitle>商机 {deals.length ? <span className='text-muted-foreground font-normal'>{deals.length}</span> : null}</CardTitle>
              {manage ? (
                <Button size='sm' variant='outline' onClick={() => setAddingDeal(true)}>
                  <Icons.add />
                  添加
                </Button>
              ) : null}
            </CardHeader>
            <CardContent>
              {deals.length ? (
                <ul className='divide-y text-sm'>
                  {deals.map((d) => (
                    <li key={d.id} className='flex items-center gap-2 py-2'>
                      <div className='min-w-0 flex-1'>
                        <Link href={`/crm/deals/view?id=${d.id}`} className='block truncate font-medium hover:underline'>
                          {d.title}
                        </Link>
                        <p className='text-muted-foreground text-xs'>
                          {d.stageName} · {formatAmount(d.amount, d.currency)}
                        </p>
                      </div>
                      <StatusBadge meta={crmDealStatus[d.status]} />
                    </li>
                  ))}
                </ul>
              ) : (
                <p className='text-muted-foreground text-sm'>还没有商机。</p>
              )}
            </CardContent>
          </Card>
        </div>
      </div>
      <ContactFormDialog open={editing} contact={contact} onOpenChange={setEditing} />
      <DealFormDialog
        open={addingDeal}
        defaults={{
          contact: { id: contact.id, name: contact.name },
          company: contact.companyId ? { id: contact.companyId, name: contact.companyName ?? '客户' } : null,
        }}
        onOpenChange={setAddingDeal}
      />
    </div>
  );
}

export default function ContactDetailPage() {
  return (
    <Suspense>
      <ContactDetail />
    </Suspense>
  );
}
