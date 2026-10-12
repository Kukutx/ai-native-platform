'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import type { CrmDealDto, CrmPipelineStageDto, CrmStageDto } from '@ainative/api-client';
import { PageHeader } from '@/components/app/page-header';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { CrmNav } from '@/features/crm/crm-nav';
import { DealFormDialog } from '@/features/crm/deal-form-dialog';
import { CloseDealDialog, useMoveDeal, type MoveRequest } from '@/features/crm/move-deal';
import { StageSettingsDialog } from '@/features/crm/stage-settings-dialog';
import { isOverdue } from '@/features/crm/utils';
import { api } from '@/lib/api';
import { formatAmount, formatDate, formatTotals } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

const DRAG_TYPE = 'application/x-crm-deal';

export default function CrmBoardPage() {
  const { can, session } = useSession();
  const [mine, setMine] = useState(false);
  const [creating, setCreating] = useState<string | null>(null);
  const [configuring, setConfiguring] = useState(false);
  const [closing, setClosing] = useState<MoveRequest | null>(null);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const ownerUserId = mine ? session?.user.id : undefined;
  const pipelineKey = queryKeys.crmPipeline(ownerUserId);
  const pipeline = useQuery({ queryKey: pipelineKey, queryFn: ({ signal }) => api.crm.deals.pipeline(ownerUserId, signal) });
  const move = useMoveDeal(pipelineKey);
  const manage = can('crm.manage');

  const requestMove = (deal: CrmDealDto, stage: CrmStageDto) => {
    if (deal.stageId === stage.id) {
      return;
    }

    // 结束商机前请对方补一句原因。
    if (stage.kind === 'open') {
      move.mutate({ deal, stage });
    } else {
      setClosing({ deal, stage });
    }
  };
  const findDeal = (id: string) => pipeline.data?.stages.flatMap((s) => s.deals).find((d) => d.id === id);
  const stages = pipeline.data?.stages.map((s) => s.stage) ?? [];

  return (
    <div className='flex min-h-0 flex-1 flex-col gap-6 p-6'>
      <PageHeader
        title='客户'
        description='销售看板：拖动商机卡片推进阶段，阶段变化会自动记入跟进记录，也可以触发自动化。'
        actions={
          <>
            <Select value={mine ? 'mine' : 'all'} onValueChange={(v) => setMine(v === 'mine')}>
              <SelectTrigger className='w-32'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value='all'>全部商机</SelectItem>
                <SelectItem value='mine'>我负责的</SelectItem>
              </SelectContent>
            </Select>
            {can('crm.configure') ? (
              <Button variant='outline' onClick={() => setConfiguring(true)} disabled={!pipeline.data}>
                <Icons.settings />
                阶段设置
              </Button>
            ) : null}
            {manage ? (
              <Button onClick={() => setCreating('')}>
                <Icons.add />
                新建商机
              </Button>
            ) : null}
          </>
        }
      />
      <CrmNav />
      {pipeline.isPending ? (
        <Skeleton className='h-64 w-full' />
      ) : pipeline.data ? (
        <>
          <div className='grid gap-4 sm:grid-cols-3'>
            <Summary label='进行中' value={formatTotals(pipeline.data.openTotals)} />
            <Summary label='加权预测' hint='按各阶段成交概率折算' value={formatTotals(pipeline.data.weightedTotals)} />
            <Summary label='本月赢单' value={formatTotals(pipeline.data.wonThisMonth)} />
          </div>
          <div className='flex min-h-0 flex-1 gap-4 overflow-x-auto pb-2'>
            {pipeline.data.stages.map((column) => (
              <div
                key={column.stage.id}
                className={cn(
                  'bg-muted/40 flex w-72 shrink-0 flex-col rounded-xl p-2 transition-colors',
                  dropTarget === column.stage.id && 'bg-primary/10 ring-primary/40 ring-2',
                )}
                onDragOver={(event) => {
                  if (manage && event.dataTransfer.types.includes(DRAG_TYPE)) {
                    event.preventDefault();
                    setDropTarget(column.stage.id);
                  }
                }}
                onDragLeave={(event) => {
                  if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
                    setDropTarget(null);
                  }
                }}
                onDrop={(event) => {
                  event.preventDefault();
                  setDropTarget(null);
                  const deal = findDeal(event.dataTransfer.getData(DRAG_TYPE));
                  if (deal) {
                    requestMove(deal, column.stage);
                  }
                }}
              >
                <ColumnHeader column={column} closedWindowDays={pipeline.data.closedWindowDays} />
                <div className='flex min-h-16 flex-col gap-2 overflow-y-auto'>
                  {column.deals.map((deal) => (
                    <DealCard key={deal.id} deal={deal} stages={stages} draggable={manage} onMove={(stage) => requestMove(deal, stage)} />
                  ))}
                  {column.dealCount > column.deals.length ? (
                    <p className='text-muted-foreground px-2 py-1 text-xs'>还有 {column.dealCount - column.deals.length} 个未显示</p>
                  ) : null}
                  {manage && column.stage.kind === 'open' ? (
                    <Button variant='ghost' size='sm' className='text-muted-foreground justify-start' onClick={() => setCreating(column.stage.id)}>
                      <Icons.add />
                      新建
                    </Button>
                  ) : null}
                </div>
              </div>
            ))}
          </div>
        </>
      ) : null}
      <DealFormDialog open={creating !== null} defaults={{ stageId: creating || undefined }} onOpenChange={(open) => !open && setCreating(null)} />
      <StageSettingsDialog open={configuring} stages={stages} onOpenChange={setConfiguring} />
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

function Summary({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card className='py-4'>
      <CardContent className='space-y-1 px-4'>
        <p className='text-muted-foreground text-xs' title={hint}>
          {label}
        </p>
        <p className='truncate text-xl font-semibold tabular-nums' title={value}>
          {value}
        </p>
      </CardContent>
    </Card>
  );
}

function ColumnHeader({ column, closedWindowDays }: { column: CrmPipelineStageDto; closedWindowDays: number }) {
  return (
    <div className='space-y-0.5 px-2 py-1.5'>
      <div className='flex items-center justify-between text-sm font-medium'>
        <span className='flex items-center gap-1.5'>
          {column.stage.kind === 'won' ? <Icons.circleCheck className='size-4 text-emerald-600' /> : null}
          {column.stage.kind === 'lost' ? <Icons.circleX className='text-muted-foreground size-4' /> : null}
          {column.stage.name}
        </span>
        <span className='text-muted-foreground'>{column.dealCount}</span>
      </div>
      <p className='text-muted-foreground truncate text-xs tabular-nums'>
        {formatTotals(column.totals, true)}
        {column.stage.kind === 'open' ? ` · ${column.stage.probability}%` : ` · 近 ${closedWindowDays} 天`}
      </p>
    </div>
  );
}

function DealCard({
  deal,
  stages,
  draggable,
  onMove,
}: {
  deal: CrmDealDto;
  stages: CrmStageDto[];
  draggable: boolean;
  onMove: (stage: CrmStageDto) => void;
}) {
  const overdue = isOverdue(deal.expectedCloseDate, deal.status);

  return (
    <div
      draggable={draggable}
      onDragStart={(event) => {
        event.dataTransfer.setData(DRAG_TYPE, deal.id);
        event.dataTransfer.effectAllowed = 'move';
      }}
      className={cn('bg-card hover:border-foreground/30 group relative rounded-lg border p-3 text-sm shadow-xs transition-colors', draggable && 'cursor-grab')}
    >
      <Link href={`/crm/deals/view?id=${deal.id}`} className='block space-y-1'>
        <p className='line-clamp-2 pr-6 font-medium'>{deal.title}</p>
        {deal.companyName ? <p className='text-muted-foreground truncate text-xs'>{deal.companyName}</p> : null}
        <div className='text-muted-foreground flex items-center justify-between gap-2 pt-1 text-xs'>
          <span className='text-foreground font-medium tabular-nums'>{formatAmount(deal.amount, deal.currency, true)}</span>
          {deal.expectedCloseDate ? <span className={cn(overdue && 'text-destructive')}>{formatDate(deal.expectedCloseDate)}</span> : null}
        </div>
        {deal.owner?.name ? <p className='text-muted-foreground text-xs'>{deal.owner.name}</p> : null}
      </Link>
      {draggable ? (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button
              size='icon'
              variant='ghost'
              className='absolute top-2 right-2 size-6 opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 data-[state=open]:opacity-100'
              aria-label='移动到'
            >
              <Icons.dots className='size-4' />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align='end'>
            <DropdownMenuLabel className='text-muted-foreground text-xs'>移动到</DropdownMenuLabel>
            <DropdownMenuSeparator />
            {stages.map((stage) => (
              <DropdownMenuItem key={stage.id} disabled={stage.id === deal.stageId} onSelect={() => onMove(stage)}>
                {stage.name}
              </DropdownMenuItem>
            ))}
          </DropdownMenuContent>
        </DropdownMenu>
      ) : null}
    </div>
  );
}
