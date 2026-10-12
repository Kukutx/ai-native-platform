'use client';

import { useDeferredValue, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ContactFormDialog } from '@/features/crm/contact-form-dialog';
import { CrmNav } from '@/features/crm/crm-nav';
import { FieldSettingsDialog } from '@/features/crm/field-settings-dialog';
import { ImportDialog } from '@/features/crm/import-dialog';
import { Pager } from '@/components/app/pager';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;

export default function CrmContactsPage() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [configuring, setConfiguring] = useState(false);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const term = useDeferredValue(search.trim());
  const contacts = useQuery({
    queryKey: queryKeys.crmContacts({ search: term, page }),
    queryFn: ({ signal }) => api.crm.contacts.list(term || undefined, undefined, undefined, undefined, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='客户'
        description='联系人：客户那边和你打交道的人，最近有往来的排在前面。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder='搜索姓名、邮箱、电话'
              className='w-56'
            />
            {can('crm.configure') ? (
              <Button variant='outline' onClick={() => setConfiguring(true)}>
                <Icons.settings />
                字段设置
              </Button>
            ) : null}
            {can('crm.manage') ? (
              <>
                <Button variant='outline' onClick={() => setImporting(true)}>
                  <Icons.upload />
                  导入
                </Button>
                <Button onClick={() => setCreating(true)}>
                  <Icons.add />
                  新建联系人
                </Button>
              </>
            ) : null}
          </>
        }
      />
      <CrmNav />
      {contacts.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : contacts.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>姓名</TableHead>
                  <TableHead>客户</TableHead>
                  <TableHead>职位</TableHead>
                  <TableHead>邮箱</TableHead>
                  <TableHead>电话</TableHead>
                  <TableHead>负责人</TableHead>
                  <TableHead>最近往来</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {contacts.data.items.map((c) => (
                  <TableRow key={c.id}>
                    <TableCell>
                      <Link href={`/crm/contacts/view?id=${c.id}`} className='font-medium hover:underline'>
                        {c.name}
                      </Link>
                    </TableCell>
                    <TableCell>
                      {c.companyId ? (
                        <Link href={`/crm/companies/view?id=${c.companyId}`} className='hover:underline'>
                          {c.companyName}
                        </Link>
                      ) : (
                        <span className='text-muted-foreground'>—</span>
                      )}
                    </TableCell>
                    <TableCell className='text-muted-foreground'>{c.title || '—'}</TableCell>
                    <TableCell>{c.email ? <a href={`mailto:${c.email}`} className='hover:underline'>{c.email}</a> : '—'}</TableCell>
                    <TableCell>{c.phone ? <a href={`tel:${c.phone}`} className='hover:underline'>{c.phone}</a> : '—'}</TableCell>
                    <TableCell className='text-muted-foreground'>{c.owner?.name ?? '—'}</TableCell>
                    <TableCell className='text-muted-foreground text-xs'>{formatTime(c.lastActivityAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={contacts.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState title={term ? '没有找到联系人' : '还没有联系人'} description={term ? '换个关键字试试。' : '联系人可以挂在客户下，也可以单独记录个人客户。'} />
      )}
      <ImportDialog open={importing} recordType='contact' onOpenChange={setImporting} />
      <FieldSettingsDialog open={configuring} recordType='contact' onOpenChange={setConfiguring} />
      <ContactFormDialog open={creating} onOpenChange={setCreating} onSaved={(c) => router.push(`/crm/contacts/view?id=${c.id}`)} />
    </div>
  );
}
