'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { CrmContactDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { OwnerSelect } from './owner-select';
import { RecordPicker, type PickedRecord } from './record-picker';
import { ownerUserId, parseTags } from './utils';

/** contact 为 null 时新建；company 用于在客户页里直接新建其联系人。 */
export function ContactFormDialog({
  open,
  contact,
  company,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  contact?: CrmContactDto | null;
  company?: PickedRecord | null;
  onOpenChange: (open: boolean) => void;
  onSaved?: (contact: CrmContactDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-xl'>
        {open ? (
          <ContactForm key={contact?.id ?? 'new'} contact={contact ?? null} company={company ?? null} onClose={() => onOpenChange(false)} onSaved={onSaved} />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function ContactForm({
  contact,
  company,
  onClose,
  onSaved,
}: {
  contact: CrmContactDto | null;
  company: PickedRecord | null;
  onClose: () => void;
  onSaved?: (contact: CrmContactDto) => void;
}) {
  const queryClient = useQueryClient();
  const { session } = useSession();
  const [form, setForm] = useState({
    name: contact?.name ?? '',
    title: contact?.title ?? '',
    email: contact?.email ?? '',
    phone: contact?.phone ?? '',
    description: contact?.description ?? '',
    tags: contact?.tags.join('，') ?? '',
  });
  const [owner, setOwner] = useState(contact ? ownerUserId(contact.owner) : session?.user.id);
  const [picked, setPicked] = useState<PickedRecord | null>(
    contact?.companyId ? { id: contact.companyId, name: contact.companyName ?? '客户' } : company,
  );
  const save = useMutation({
    mutationFn: () => {
      const request = { ...form, tags: parseTags(form.tags), companyId: picked?.id, ownerUserId: owner, expectedUpdatedAt: contact?.updatedAt };
      return contact ? api.crm.contacts.update(contact.id, request) : api.crm.contacts.create(request);
    },
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      onSaved?.(saved);
      onClose();
    },
    onError: () => queryClient.invalidateQueries({ queryKey: ['crm'] }),
  });
  const field = (key: keyof typeof form) => ({
    value: form[key],
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
        <DialogTitle>{contact ? '编辑联系人' : '新建联系人'}</DialogTitle>
      </DialogHeader>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='contact-name'>姓名</Label>
          <Input id='contact-name' {...field('name')} required maxLength={100} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='contact-title'>职位</Label>
          <Input id='contact-title' {...field('title')} maxLength={100} />
        </div>
      </div>
      <div className='grid gap-2'>
        <Label>所属客户</Label>
        <RecordPicker kind='company' value={picked} onChange={setPicked} placeholder='搜索客户；个人客户可以不选' />
      </div>
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='contact-email'>邮箱</Label>
          <Input id='contact-email' type='email' {...field('email')} maxLength={254} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='contact-phone'>电话</Label>
          <Input id='contact-phone' {...field('phone')} maxLength={50} />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='contact-tags'>标签</Label>
          <Input id='contact-tags' {...field('tags')} placeholder='用逗号分隔' />
        </div>
        <OwnerSelect value={owner} onChange={setOwner} allowNone={!!contact} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='contact-description'>备注</Label>
        <Textarea id='contact-description' {...field('description')} rows={3} placeholder='负责什么、偏好、关系背景等' />
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
