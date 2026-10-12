'use client';

import { useDeferredValue, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { CompanyFormDialog } from '@/features/crm/company-form-dialog';
import { CrmNav } from '@/features/crm/crm-nav';
import { FieldSettingsDialog } from '@/features/crm/field-settings-dialog';
import { ImportDialog } from '@/features/crm/import-dialog';
import { Pager } from '@/features/crm/pager';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;

export default function CrmCompaniesPage() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [configuring, setConfiguring] = useState(false);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const term = useDeferredValue(search.trim());
  const companies = useQuery({
    queryKey: queryKeys.crmCompanies({ search: term, page }),
    queryFn: ({ signal }) => api.crm.companies.list(term || undefined, undefined, undefined, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='客户'
        description='客户（公司或组织）及其联系人、商机和往来记录，最近有往来的排在前面。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => {
                setSearch(e.target.value);
                setPage(1);
              }}
              placeholder='搜索名称、行业、电话'
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
                  新建客户
                </Button>
              </>
            ) : null}
          </>
        }
      />
      <CrmNav />
      {companies.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : companies.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>名称</TableHead>
                  <TableHead>行业</TableHead>
                  <TableHead className='text-right'>联系人</TableHead>
                  <TableHead className='text-right'>进行中商机</TableHead>
                  <TableHead>负责人</TableHead>
                  <TableHead>最近往来</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {companies.data.items.map((c) => (
                  <TableRow key={c.id}>
                    <TableCell className='max-w-xs'>
                      <Link href={`/crm/companies/view?id=${c.id}`} className='font-medium hover:underline'>
                        {c.name}
                      </Link>
                      {c.tags.length ? (
                        <div className='mt-1 flex flex-wrap gap-1'>
                          {c.tags.map((tag) => (
                            <Badge key={tag} variant='outline' className='text-xs font-normal'>
                              {tag}
                            </Badge>
                          ))}
                        </div>
                      ) : null}
                    </TableCell>
                    <TableCell className='text-muted-foreground'>{c.industry || '—'}</TableCell>
                    <TableCell className='text-right tabular-nums'>{c.contactCount}</TableCell>
                    <TableCell className='text-right tabular-nums'>{c.openDealCount}</TableCell>
                    <TableCell className='text-muted-foreground'>{c.owner?.name ?? '—'}</TableCell>
                    <TableCell className='text-muted-foreground text-xs'>{formatTime(c.lastActivityAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={companies.data.total} onChange={setPage} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState title={term ? '没有找到客户' : '还没有客户'} description={term ? '换个关键字试试。' : '新建第一个客户，或者让智能体从会议纪要里整理录入。'} />
      )}
      <ImportDialog open={importing} recordType='company' onOpenChange={setImporting} />
      <FieldSettingsDialog open={configuring} recordType='company' onOpenChange={setConfiguring} />
      <CompanyFormDialog open={creating} onOpenChange={setCreating} onSaved={(c) => router.push(`/crm/companies/view?id=${c.id}`)} />
    </div>
  );
}
