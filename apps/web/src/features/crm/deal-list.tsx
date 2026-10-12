'use client';

import Link from 'next/link';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { CrmDealStatus } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { Pager } from '@/components/app/pager';
import { ErrorState } from '@/components/app/query-state';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { pickOption, useSearchText } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { catalogQueries } from '@/lib/catalogs';
import { crmDealStatus, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';
import { isOverdue } from './utils';

const PAGE_SIZE = 50;
const ALL = '__all__';
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export interface DealListFilters {
  q: string;
  stage: string;
  status: string;
  page: number;
}

/**
 * 商机列表：按最近更新排序，可以按关键词、阶段、状态筛选并分页；ownerUserId 由上层的「我负责的」决定。
 */
export function DealList({
  filters,
  onChange,
  ownerUserId,
}: {
  filters: DealListFilters;
  onChange: (patch: Partial<DealListFilters>) => void;
  ownerUserId?: string;
}) {
  const stages = useQuery(catalogQueries.crmStages());
  const [search, setSearch] = useSearchText(filters.q, (q) => onChange({ q, page: 1 }));
  const status: CrmDealStatus | undefined = pickOption(filters.status, crmDealStatus);
  // 地址里的阶段编号可能被手工改坏：不是编号格式时忽略，免得请求被拒绝。
  const stageId = GUID.test(filters.stage) ? filters.stage : undefined;
  const page = Math.max(1, filters.page);
  const term = filters.q || undefined;
  const deals = useQuery({
    queryKey: queryKeys.crmDeals({ search: term, stageId, status, ownerUserId, page }),
    queryFn: ({ signal }) => api.crm.deals.list(term, stageId, status, undefined, undefined, ownerUserId, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });
  const filtered = !!(term || stageId || status);

  return (
    <div className='space-y-4'>
      <div className='flex flex-wrap items-center gap-2' role='search' aria-label='筛选商机'>
        <div className='relative'>
          <Icons.search className='text-muted-foreground pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2' />
          <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder='搜索名称、客户、联系人' className='w-56 pl-8' aria-label='搜索商机' />
        </div>
        <Select value={stageId ?? ALL} onValueChange={(v) => onChange({ stage: v === ALL ? '' : v, page: 1 })}>
          <SelectTrigger className='w-36' aria-label='按阶段筛选'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>全部阶段</SelectItem>
            {stages.data?.map((s) => (
              <SelectItem key={s.id} value={s.id}>
                {s.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Select value={status ?? ALL} onValueChange={(v) => onChange({ status: v === ALL ? '' : v, page: 1 })}>
          <SelectTrigger className='w-28' aria-label='按状态筛选'>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>全部状态</SelectItem>
            {Object.entries(crmDealStatus).map(([key, meta]) => (
              <SelectItem key={key} value={key}>
                {meta.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        {filtered ? (
          <Button variant='ghost' size='sm' onClick={() => onChange({ q: '', stage: '', status: '', page: 1 })}>
            <Icons.close />
            清除筛选
          </Button>
        ) : null}
      </div>
      {deals.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : deals.isError && !deals.data ? (
        <ErrorState query={deals} />
      ) : deals.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>名称</TableHead>
                  <TableHead>客户</TableHead>
                  <TableHead>阶段</TableHead>
                  <TableHead className='text-right'>金额</TableHead>
                  <TableHead>负责人</TableHead>
                  <TableHead>预计成交日期</TableHead>
                  <TableHead>更新时间</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {deals.data.items.map((deal) => (
                  <TableRow key={deal.id}>
                    <TableCell className='max-w-xs'>
                      <Link href={`/crm/deals/view?id=${deal.id}`} className='line-clamp-2 font-medium whitespace-normal hover:underline'>
                        {deal.title}
                      </Link>
                      {deal.contactName ? <span className='text-muted-foreground block truncate text-xs'>联系人：{deal.contactName}</span> : null}
                    </TableCell>
                    <TableCell className='max-w-48'>
                      {deal.companyId ? (
                        <Link href={`/crm/companies/view?id=${deal.companyId}`} className='block truncate hover:underline'>
                          {deal.companyName}
                        </Link>
                      ) : (
                        <span className='text-muted-foreground'>—</span>
                      )}
                    </TableCell>
                    <TableCell>
                      <div className='flex flex-wrap items-center gap-1.5'>
                        <span>{deal.stageName}</span>
                        {deal.status === 'open' ? (
                          <span className='text-muted-foreground text-xs tabular-nums'>{deal.probability}%</span>
                        ) : (
                          <StatusBadge meta={crmDealStatus[deal.status]} />
                        )}
                      </div>
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{formatAmount(deal.amount, deal.currency)}</TableCell>
                    <TableCell className='text-muted-foreground'>{deal.owner?.name ?? '—'}</TableCell>
                    <TableCell className={cn('tabular-nums', isOverdue(deal.expectedCloseDate, deal.status) ? 'text-destructive' : 'text-muted-foreground')}>
                      {formatDate(deal.expectedCloseDate)}
                    </TableCell>
                    <TableCell className='text-muted-foreground text-xs'>{formatTime(deal.updatedAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={deals.data.total} onChange={(next) => onChange({ page: next })} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState
          title={filtered || ownerUserId ? '没有符合条件的商机' : '还没有商机'}
          description={filtered || ownerUserId ? '换个条件试试。' : '新建第一个商机，或者让智能体从会议纪要里整理录入。'}
        />
      )}
    </div>
  );
}
