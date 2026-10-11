'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { CrmCompanyDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { OwnerSelect } from './owner-select';
import { ownerUserId, parseTags } from './utils';

/** company 为 null 时新建，否则编辑。 */
export function CompanyFormDialog({
  open,
  company,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  company?: CrmCompanyDto | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (company: CrmCompanyDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-xl'>
        {open ? <CompanyForm key={company?.id ?? 'new'} company={company ?? null} onClose={() => onOpenChange(false)} onSaved={onSaved} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function CompanyForm({ company, onClose, onSaved }: { company: CrmCompanyDto | null; onClose: () => void; onSaved?: (company: CrmCompanyDto) => void }) {
  const queryClient = useQueryClient();
  const { session } = useSession();
  const [form, setForm] = useState({
    name: company?.name ?? '',
    industry: company?.industry ?? '',
    phone: company?.phone ?? '',
    website: company?.website ?? '',
    address: company?.address ?? '',
    description: company?.description ?? '',
    tags: company?.tags.join('，') ?? '',
    owner: company ? ownerUserId(company.owner) : session?.user.id,
  });
  const save = useMutation({
    mutationFn: () => {
      const request = { ...form, tags: parseTags(form.tags), ownerUserId: form.owner, expectedUpdatedAt: company?.updatedAt };
      return company ? api.crm.companies.update(company.id, request) : api.crm.companies.create(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      onSaved?.(saved);
      onClose();
    },
    // 冲突时刷新，下次打开就是最新内容。
    onError: () => queryClient.invalidateQueries({ queryKey: ['crm'] }),
  });
  const field = (key: keyof typeof form) => ({
    value: (form[key] as string | undefined) ?? '',
    onChange: (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm({ ...form, [key]: e.target.value }),
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
        <DialogTitle>{company ? '编辑客户' : '新建客户'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='company-name'>名称</Label>
        <Input id='company-name' {...field('name')} required maxLength={200} placeholder='公司或组织全称' />
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='company-industry'>行业</Label>
          <Input id='company-industry' {...field('industry')} maxLength={100} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='company-phone'>电话</Label>
          <Input id='company-phone' {...field('phone')} maxLength={50} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='company-website'>官网</Label>
          <Input id='company-website' {...field('website')} maxLength={300} placeholder='https://' />
        </div>
        <OwnerSelect value={form.owner} onChange={(owner) => setForm({ ...form, owner })} allowNone={!!company} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='company-address'>地址</Label>
        <Input id='company-address' {...field('address')} maxLength={500} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='company-tags'>标签</Label>
        <Input id='company-tags' {...field('tags')} placeholder='用逗号分隔，例如：重点客户，华东' />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='company-description'>简介</Label>
        <Textarea id='company-description' {...field('description')} rows={4} placeholder='业务、规模、合作背景等' />
      </div>
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
