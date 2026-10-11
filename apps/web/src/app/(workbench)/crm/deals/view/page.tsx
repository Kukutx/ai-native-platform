'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CrmDealDto, CrmStageDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Skeleton } from '@/components/ui/skeleton';
import { ActivityTimeline } from '@/features/crm/activity-timeline';
import { DealFormDialog } from '@/features/crm/deal-form-dialog';
import { InfoRow } from '@/features/crm/info-row';
import { CloseDealDialog, useMoveDeal, type MoveRequest } from '@/features/crm/move-deal';
import { isOverdue } from '@/features/crm/utils';
import { api } from '@/lib/api';
import { crmDealStatus, crmStageKind, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

function DealDetail() {
  const id = useSearchParams().get('id') ?? '';
  const router = useRouter();
  const { can } = useSession();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [closing, setClosing] = useState<MoveRequest | null>(null);
  const detail = useQuery({ queryKey: queryKeys.crmDeal(id), queryFn: ({ signal }) => api.crm.deals.get(id, signal), enabled: !!id });
  const stages = useQuery({ queryKey: queryKeys.crmStages, queryFn: ({ signal }) => api.crm.stages.list(signal) });
  const move = useMoveDeal();
  const remove = useMutation({
    mutationFn: () => api.crm.deals.delete(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      router.replace('/crm');
    },
  });

  if (!detail.data) {
    return <Skeleton className='m-6 h-40' />;
  }

  const { deal } = detail.data;
  const manage = can('crm.manage');
  const requestMove = (stage: CrmStageDto) => {
    if (stage.id === deal.stageId || move.isPending) {
      return;
    }

    if (stage.kind === 'open') {
      move.mutate({ deal, stage });
    } else {
      setClosing({ deal, stage });
    }
  };

  return (
    <div className='space-y-6 p-6'>
      <Link href='/crm' className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        销售看板
      </Link>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <div className='space-y-2'>
          <div className='flex items-center gap-2'>
            <h1 className='text-2xl font-semibold tracking-tight'>{deal.title}</h1>
            <StatusBadge meta={crmDealStatus[deal.status]} />
          </div>
          <div className='text-muted-foreground flex flex-wrap items-center gap-3 text-sm'>
            {deal.companyId ? (
              <Link href={`/crm/companies/view?id=${deal.companyId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.building className='size-4' />
                {deal.companyName}
              </Link>
            ) : null}
            {deal.contactId ? (
              <Link href={`/crm/contacts/view?id=${deal.contactId}`} className='text-foreground inline-flex items-center gap-1 hover:underline'>
                <Icons.user className='size-4' />
                {deal.contactName}
              </Link>
            ) : null}
          </div>
        </div>
        {manage ? (
          <div className='flex gap-2'>
            <Button variant='outline' onClick={() => setEditing(true)}>
              <Icons.edit />
              编辑
            </Button>
            <Button variant='ghost' disabled={remove.isPending} onClick={() => confirm(`删除商机「${deal.title}」？它的阶段变化记录一并删除。`) && remove.mutate()}>
              <Icons.trash />
              删除
            </Button>
          </div>
        ) : null}
      </div>
      {stages.data ? <StageBar deal={deal} stages={stages.data} disabled={!manage || move.isPending} onSelect={requestMove} /> : null}
      <div className='grid gap-6 lg:grid-cols-3'>
        <div className='space-y-6 lg:col-span-2'>
          {deal.description ? (
            <Card>
              <CardContent className='text-sm whitespace-pre-wrap'>{deal.description}</CardContent>
            </Card>
          ) : null}
          <ActivityTimeline scope={{ dealId: deal.id }} />
        </div>
        <Card className='h-fit'>
          <CardHeader>
            <CardTitle>详情</CardTitle>
          </CardHeader>
          <CardContent className='space-y-3 text-sm'>
            <InfoRow icon={Icons.creditCard} label='金额'>
              <span className='font-medium tabular-nums'>{formatAmount(deal.amount, deal.currency)}</span>
            </InfoRow>
            <InfoRow icon={Icons.trendingUp} label='概率'>
              {deal.status === 'open' ? `${deal.probability}%` : crmDealStatus[deal.status].label}
            </InfoRow>
            <InfoRow icon={Icons.calendar} label='预计成交'>
              {deal.expectedCloseDate ? (
                <span className={cn(isOverdue(deal.expectedCloseDate, deal.status) && 'text-destructive')}>{formatDate(deal.expectedCloseDate)}</span>
              ) : null}
            </InfoRow>
            <InfoRow icon={Icons.user} label='负责人'>
              {deal.owner?.name ?? null}
            </InfoRow>
            <div className='text-muted-foreground space-y-1 border-t pt-3 text-xs'>
              <p>
                {deal.createdBy.name ?? '系统'} 创建于 {formatTime(deal.createdAt)}
              </p>
              {deal.closedAt ? <p>结束于 {formatTime(deal.closedAt)}</p> : null}
            </div>
          </CardContent>
        </Card>
      </div>
      <DealFormDialog open={editing} deal={deal} onOpenChange={setEditing} />
      <CloseDealDialog
        request={closing}
        onCancel={() => setClosing(null)}
        onConfirm={(note) => {
          if (closing) {
            move.mutate({ ...closing, note: note || undefined });
          }

          setClosing(null);
        }}
      />
    </div>
  );
}

/** 阶段条：进行中的阶段依次排开，赢单、输单放在末尾；点一下即推进。 */
function StageBar({ deal, stages, disabled, onSelect }: { deal: CrmDealDto; stages: CrmStageDto[]; disabled: boolean; onSelect: (stage: CrmStageDto) => void }) {
  const current = stages.find((s) => s.id === deal.stageId);
  const reached = (stage: CrmStageDto) => current?.kind === 'open' && stage.kind === 'open' && stage.position <= current.position;

  return (
    <div className='flex flex-wrap gap-1.5'>
      {stages.map((stage) => {
        const active = stage.id === deal.stageId;
        return (
          <button
            key={stage.id}
            type='button'
            disabled={disabled || active}
            onClick={() => onSelect(stage)}
            className={cn(
              'min-w-24 flex-1 rounded-md border px-3 py-2 text-left text-sm transition-colors disabled:cursor-default',
              active && stage.kind === 'won' && 'border-emerald-600 bg-emerald-600 text-white',
              active && stage.kind === 'lost' && 'bg-muted-foreground border-muted-foreground text-background',
              active && stage.kind === 'open' && 'bg-primary border-primary text-primary-foreground',
              !active && reached(stage) && 'bg-primary/10 border-primary/30',
              !active && !disabled && 'hover:border-foreground/40',
            )}
            title={active ? '当前阶段' : `移到「${stage.name}」`}
          >
            <span className='block truncate font-medium'>{stage.name}</span>
            <span className={cn('text-xs', active ? 'opacity-80' : 'text-muted-foreground')}>
              {stage.kind === 'open' ? `${stage.probability}%` : stage.name === crmStageKind[stage.kind] ? '结束商机' : crmStageKind[stage.kind]}
            </span>
          </button>
        );
      })}
    </div>
  );
}

export default function DealDetailPage() {
  return (
    <Suspense>
      <DealDetail />
    </Suspense>
  );
}
