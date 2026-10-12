'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CrmDealDto } from '@ainative/api-client';
import { CurrencySelect } from '@/components/app/currency-select';
import { EditConflictAlert, isEditConflict, ReloadableForm, useEditVersion } from '@/components/app/edit-conflict';
import { useSession } from '@/components/app/session-provider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';
import { CustomFieldInputs, useCustomFieldForm } from './custom-fields';
import { OwnerSelect } from './owner-select';
import { RecordPicker, type PickedRecord } from './record-picker';
import { ownerUserId } from './utils';

export interface DealDefaults {
  company?: PickedRecord | null;
  contact?: PickedRecord | null;
  stageId?: string;
}

/** deal 为 null 时新建；推进阶段不在这里改，用看板拖动或详情页的阶段条。 */
export function DealFormDialog({
  open,
  deal,
  defaults,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  deal?: CrmDealDto | null;
  defaults?: DealDefaults;
  onOpenChange: (open: boolean) => void;
  onSaved?: (deal: CrmDealDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='max-h-[90vh] overflow-y-auto sm:max-w-xl'>
        {open ? (
          <ReloadableForm entity={deal ?? null}>
            {(current, reload, key) => (
              <DealForm key={key} deal={current} defaults={defaults} onReload={reload} onClose={() => onOpenChange(false)} onSaved={onSaved} />
            )}
          </ReloadableForm>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function DealForm({
  deal,
  defaults,
  onReload,
  onClose,
  onSaved,
}: {
  deal: CrmDealDto | null;
  defaults?: DealDefaults;
  onReload: (latest: CrmDealDto) => void;
  onClose: () => void;
  onSaved?: (deal: CrmDealDto) => void;
}) {
  const queryClient = useQueryClient();
  const edit = useEditVersion(deal, async () => {
    const id = deal!.id;
    const detail = await queryClient.fetchQuery({ queryKey: queryKeys.crmDeal(id), queryFn: ({ signal }) => api.crm.deals.get(id, signal), staleTime: 0 });
    return detail.deal;
  });
  const { session } = useSession();
  const stages = useQuery({ queryKey: queryKeys.crmStages, queryFn: ({ signal }) => api.crm.stages.list(signal), enabled: !deal });
  const [title, setTitle] = useState(deal?.title ?? '');
  const [company, setCompany] = useState<PickedRecord | null>(
    deal?.companyId ? { id: deal.companyId, name: deal.companyName ?? '客户' } : (defaults?.company ?? null),
  );
  const [contact, setContact] = useState<PickedRecord | null>(
    deal?.contactId ? { id: deal.contactId, name: deal.contactName ?? '联系人' } : (defaults?.contact ?? null),
  );
  const [stageId, setStageId] = useState(defaults?.stageId);
  const [amount, setAmount] = useState(deal?.amount?.toString() ?? '');
  const [currency, setCurrency] = useState(deal?.currency ?? 'CNY');
  const [expectedCloseDate, setExpectedCloseDate] = useState(deal?.expectedCloseDate ?? '');
  const [description, setDescription] = useState(deal?.description ?? '');
  const [owner, setOwner] = useState(deal ? ownerUserId(deal.owner) : session?.user.id);
  const custom = useCustomFieldForm('deal', deal?.customFields);
  const save = useMutation({
    mutationFn: () => {
      const request = {
        title,
        companyId: company?.id,
        contactId: contact?.id,
        stageId: deal ? undefined : stageId,
        amount: amount.trim() ? Number(amount) : undefined,
        currency,
        expectedCloseDate: expectedCloseDate || undefined,
        description,
        ownerUserId: owner,
        customFields: custom.request(),
        expectedUpdatedAt: edit.expected(),
      };
      return deal ? api.crm.deals.update(deal.id, request) : api.crm.deals.create(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      onSaved?.(saved);
      onClose();
    },
    meta: { handledError: isEditConflict },
    // 冲突时保留表单里的修改，由用户选择载入最新或仍然覆盖；页面上的数据照常刷新。
    onError: (error) => {
      edit.onError(error);
      void queryClient.invalidateQueries({ queryKey: ['crm'] });
    },
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{deal ? '编辑商机' : '新建商机'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='deal-title'>名称</Label>
        <Input id='deal-title' value={title} onChange={(e) => setTitle(e.target.value)} required maxLength={200} placeholder='例如：星火科技 2027 年度软件采购' />
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label>客户</Label>
          <RecordPicker
            kind='company'
            value={company}
            onChange={(next) => {
              setCompany(next);
              // 换了客户，原来的联系人多半不属于新客户。
              if (next?.id !== company?.id) {
                setContact(null);
              }
            }}
          />
        </div>
        <div className='grid gap-2'>
          <Label>主要联系人</Label>
          <RecordPicker kind='contact' value={contact} onChange={setContact} companyId={company?.id} />
        </div>
        {deal ? null : (
          <div className='grid gap-2'>
            <Label>阶段</Label>
            <Select value={stageId ?? stages.data?.find((s) => s.kind === 'open')?.id ?? ''} onValueChange={setStageId}>
              <SelectTrigger className='w-full'>
                <SelectValue placeholder='第一个进行中的阶段' />
              </SelectTrigger>
              <SelectContent>
                {stages.data?.map((s) => (
                  <SelectItem key={s.id} value={s.id}>
                    {s.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}
        <div className='grid gap-2'>
          <Label htmlFor='deal-close'>预计成交日期</Label>
          <Input id='deal-close' type='date' value={expectedCloseDate} onChange={(e) => setExpectedCloseDate(e.target.value)} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='deal-amount'>预计金额</Label>
          <div className='flex gap-2'>
            <Input id='deal-amount' type='number' min={0} step='0.01' value={amount} onChange={(e) => setAmount(e.target.value)} className='flex-1' />
            <CurrencySelect value={currency} onChange={setCurrency} />
          </div>
        </div>
        <OwnerSelect value={owner} onChange={setOwner} allowNone={!!deal} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='deal-description'>描述</Label>
        <Textarea id='deal-description' value={description} onChange={(e) => setDescription(e.target.value)} rows={4} placeholder='需求、竞争情况、关键决策人等' />
      </div>
      <CustomFieldInputs fields={custom.fields} values={custom.values} onChange={custom.onChange} />
      <EditConflictAlert edit={edit} subject='商机' onLoadLatest={onReload} onOverwrite={() => save.mutate()} disabled={save.isPending} />
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          保存
        </Button>
      </DialogFooter>
    </form>
  );
}
