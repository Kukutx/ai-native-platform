'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { ActivityTimeline } from '@/features/crm/activity-timeline';
import { CompanyFormDialog } from '@/features/crm/company-form-dialog';
import { ContactFormDialog } from '@/features/crm/contact-form-dialog';
import { CustomFieldValues } from '@/features/crm/custom-fields';
import { DealFormDialog } from '@/features/crm/deal-form-dialog';
import { InfoRow } from '@/components/app/info-row';
import { ListLimitNote } from '@/components/app/list-limit-note';
import { api } from '@/lib/api';
import { crmDealStatus, formatAmount, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

function CompanyDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const [editing, setEditing] = useState(false);
  const [addingContact, setAddingContact] = useState(false);
  const [addingDeal, setAddingDeal] = useState(false);
  const detail = useQuery({ queryKey: queryKeys.crmCompany(id), queryFn: ({ signal }) => api.crm.companies.get(id, signal), enabled: !!id });
  const remove = useMutation({
    mutationFn: () => api.crm.companies.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      router.replace('/crm/companies');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const { company, contacts, deals, contactTotal, dealTotal } = detail.data;
  const manage = can('crm.manage');
  const picked = { id: company.id, name: company.name };

  return (
    <div className='space-y-6 p-6'>
      <Link href='/crm/companies' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        客户
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <h1 className='flex items-center gap-2 text-2xl font-semibold tracking-tight'>
            <Icons.building className='text-muted-foreground size-6' />
            {company.name}
          </h1>
          <div className='flex flex-wrap items-center gap-2 text-sm'>
            {company.industry ? <span className='text-muted-foreground'>{company.industry}</span> : null}
            {company.tags.map((tag) => (
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
            <Button
              variant='ghost'
              disabled={remove.isPending}
              onClick={() =>
                void confirm({ title: `删除客户「${company.name}」？`, description: '联系人和商机会保留，只解除与它的关联。', confirmLabel: '删除' }).then(
                  (ok) => ok && remove.mutate(),
                )
              }
            >
              <Icons.trash />
              删除
            </Button>
          </div>
        ) : null}
      </div>
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          <ActivityTimeline scope={{ companyId: company.id }} />
        </div>
        <div className='space-y-6'>
          <Card>
            <CardHeader>
              <CardTitle>资料</CardTitle>
            </CardHeader>
            <CardContent className='space-y-3 text-sm'>
              <InfoRow icon={Icons.phone} label='电话'>
                {company.phone ? <a href={`tel:${company.phone}`} className='hover:underline'>{company.phone}</a> : null}
              </InfoRow>
              <InfoRow icon={Icons.world} label='官网'>
                {company.website ? (
                  <a href={/^https?:\/\//i.test(company.website) ? company.website : `https://${company.website}`} target='_blank' rel='noreferrer' className='break-all hover:underline'>
                    {company.website}
                  </a>
                ) : null}
              </InfoRow>
              <InfoRow icon={Icons.mapPin} label='地址'>
                {company.address || null}
              </InfoRow>
              <InfoRow icon={Icons.user} label='负责人'>
                {company.owner?.name ?? null}
              </InfoRow>
              <CustomFieldValues recordType='company' values={company.customFields} />
              {company.description ? <p className='text-muted-foreground border-t pt-3 whitespace-pre-wrap'>{company.description}</p> : null}
              <p className='text-muted-foreground border-t pt-3 text-xs'>
                {company.createdBy.name ?? '系统'} 创建于 {formatTime(company.createdAt)}
              </p>
            </CardContent>
          </Card>
          <Card>
            <CardHeader className='flex flex-row items-center justify-between'>
              <CardTitle>联系人 {contactTotal ? <span className='text-muted-foreground font-normal'>{contactTotal}</span> : null}</CardTitle>
              {manage ? (
                <Button size='sm' variant='outline' onClick={() => setAddingContact(true)}>
                  <Icons.add />
                  添加
                </Button>
              ) : null}
            </CardHeader>
            <CardContent>
              {contacts.length ? (
                <ul className='divide-y text-sm'>
                  {contacts.map((c) => (
                    <li key={c.id} className='py-2'>
                      <Link href={`/crm/contacts/view?id=${c.id}`} className='font-medium hover:underline'>
                        {c.name}
                      </Link>
                      <p className='text-muted-foreground text-xs'>{[c.title, c.phone, c.email].filter(Boolean).join(' · ') || '—'}</p>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className='text-muted-foreground text-sm'>还没有联系人。</p>
              )}
              <ListLimitNote shown={contacts.length} total={contactTotal} />
            </CardContent>
          </Card>
          <Card>
            <CardHeader className='flex flex-row items-center justify-between'>
              <CardTitle>商机 {dealTotal ? <span className='text-muted-foreground font-normal'>{dealTotal}</span> : null}</CardTitle>
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
              <ListLimitNote shown={deals.length} total={dealTotal} />
            </CardContent>
          </Card>
        </div>
      </div>
      <CompanyFormDialog open={editing} company={company} onOpenChange={setEditing} />
      <ContactFormDialog open={addingContact} company={picked} onOpenChange={setAddingContact} />
      <DealFormDialog open={addingDeal} defaults={{ company: picked }} onOpenChange={setAddingDeal} />
    </div>
  );
}

export default function CompanyDetailPage() {
  return (
    <Suspense>
      <CompanyDetail />
    </Suspense>
  );
}
