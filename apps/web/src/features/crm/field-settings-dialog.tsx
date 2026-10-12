'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { CrmFieldDto, CrmFieldType, CrmRecordType } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Switch } from '@/components/ui/switch';
import { Tabs, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { crmFieldType, crmRecordType } from '@/lib/labels';
import { useCrmFields } from './custom-fields';

const MAX_FIELDS = 50;

/** 自定义字段设置：按记录类型分页签，增删改字段、调整顺序。 */
export function FieldSettingsDialog({
  open,
  recordType,
  onOpenChange,
}: {
  open: boolean;
  recordType: CrmRecordType;
  onOpenChange: (open: boolean) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='max-h-[90vh] overflow-y-auto sm:max-w-2xl'>
        {open ? <FieldSettings initialType={recordType} onClose={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function FieldSettings({ initialType, onClose }: { initialType: CrmRecordType; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [recordType, setRecordType] = useState(initialType);
  // undefined 为列表；null 为新建；否则为正在编辑的字段。
  const [editing, setEditing] = useState<CrmFieldDto | null | undefined>(undefined);
  const fields = useCrmFields(recordType);
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['crm'] });
  const remove = useMutation({ mutationFn: (field: CrmFieldDto) => api.crm.fields.delete(field.id), onSettled: refresh });
  const reorder = useMutation({ mutationFn: (ids: string[]) => api.crm.fields.reorder({ recordType, ids }), onSettled: refresh });
  const list = fields.data ?? [];
  const move = (index: number, offset: number) => {
    const ids = list.map((f) => f.id);
    const [id] = ids.splice(index, 1);
    ids.splice(index + offset, 0, id);
    reorder.mutate(ids);
  };

  return (
    <div className='grid gap-4'>
      <DialogHeader>
        <DialogTitle>自定义字段</DialogTitle>
        <DialogDescription>
          为客户、联系人和商机添加自己需要的字段，新建、编辑和导入时填写。字段标识供智能体和工作流读写（例如 customFields.region），创建后不能修改。
        </DialogDescription>
      </DialogHeader>
      <Tabs
        value={recordType}
        onValueChange={(v) => {
          setRecordType(v as CrmRecordType);
          setEditing(undefined);
        }}
      >
        <TabsList>
          {(Object.keys(crmRecordType) as CrmRecordType[]).map((type) => (
            <TabsTrigger key={type} value={type}>
              {crmRecordType[type]}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>
      {editing !== undefined ? (
        <FieldEditor key={editing?.id ?? 'new'} recordType={recordType} field={editing} count={list.length} onDone={() => setEditing(undefined)} />
      ) : fields.isPending ? (
        <Skeleton className='h-32 w-full' />
      ) : (
        <div className='grid gap-2'>
          {list.length ? (
            <ul className='divide-y rounded-md border'>
              {list.map((field, index) => (
                <li key={field.id} className='flex items-center gap-3 px-3 py-2'>
                  <div className='min-w-0 flex-1'>
                    <div className='flex flex-wrap items-center gap-2'>
                      <span className='font-medium'>{field.label}</span>
                      <Badge variant='secondary' className='font-normal'>
                        {crmFieldType[field.type].label}
                      </Badge>
                      {field.required ? (
                        <Badge variant='outline' className='font-normal'>
                          必填
                        </Badge>
                      ) : null}
                    </div>
                    <p className='text-muted-foreground truncate text-xs'>
                      <code>{field.key}</code>
                      {field.options.length ? ` · ${field.options.join('、')}` : null}
                    </p>
                  </div>
                  <div className='flex shrink-0'>
                    <Button type='button' size='icon' variant='ghost' className='size-8' disabled={index === 0 || reorder.isPending} onClick={() => move(index, -1)} aria-label={`上移 ${field.label}`}>
                      <Icons.arrowUp className='size-4' />
                    </Button>
                    <Button
                      type='button'
                      size='icon'
                      variant='ghost'
                      className='size-8'
                      disabled={index === list.length - 1 || reorder.isPending}
                      onClick={() => move(index, 1)}
                      aria-label={`下移 ${field.label}`}
                    >
                      <Icons.arrowDown className='size-4' />
                    </Button>
                    <Button type='button' size='icon' variant='ghost' className='size-8' onClick={() => setEditing(field)} aria-label={`编辑 ${field.label}`}>
                      <Icons.edit className='size-4' />
                    </Button>
                    <Button
                      type='button'
                      size='icon'
                      variant='ghost'
                      className='size-8'
                      disabled={remove.isPending}
                      onClick={() => confirm(`删除字段「${field.label}」？所有${crmRecordType[recordType]}里这个字段的值都会被清除，不能恢复。`) && remove.mutate(field)}
                      aria-label={`删除 ${field.label}`}
                    >
                      <Icons.trash className='size-4' />
                    </Button>
                  </div>
                </li>
              ))}
            </ul>
          ) : (
            <p className='text-muted-foreground rounded-md border border-dashed px-3 py-6 text-center text-sm'>{crmRecordType[recordType]}还没有自定义字段。</p>
          )}
          <Button type='button' variant='outline' size='sm' className='w-fit' disabled={list.length >= MAX_FIELDS} onClick={() => setEditing(null)}>
            <Icons.add />
            添加字段
          </Button>
        </div>
      )}
      {editing === undefined ? (
        <DialogFooter>
          <Button type='button' variant='outline' onClick={onClose}>
            完成
          </Button>
        </DialogFooter>
      ) : null}
    </div>
  );
}

function FieldEditor({ recordType, field, count, onDone }: { recordType: CrmRecordType; field: CrmFieldDto | null; count: number; onDone: () => void }) {
  const queryClient = useQueryClient();
  const [form, setForm] = useState({
    label: field?.label ?? '',
    key: field?.key ?? `field_${count + 1}`,
    type: field?.type ?? ('text' as CrmFieldType),
    options: field?.options.join('\n') ?? '',
    required: field?.required ?? false,
    description: field?.description ?? '',
  });
  const hasOptions = form.type === 'select' || form.type === 'multi_select';
  const save = useMutation({
    mutationFn: () => {
      const request = {
        recordType,
        key: form.key.trim(),
        label: form.label.trim(),
        type: form.type,
        options: hasOptions ? form.options.split('\n').map((o) => o.trim()).filter(Boolean) : [],
        required: form.type !== 'checkbox' && form.required,
        description: form.description.trim(),
      };
      return field ? api.crm.fields.update(field.id, request) : api.crm.fields.create(request);
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      onDone();
    },
  });

  return (
    <form
      className='grid gap-4 rounded-md border p-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <div className='grid gap-4 sm:grid-cols-2'>
        <div className='grid gap-2'>
          <Label htmlFor='field-label'>名称</Label>
          <Input id='field-label' value={form.label} onChange={(e) => setForm({ ...form, label: e.target.value })} required maxLength={50} placeholder='例如：所属区域' />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='field-key'>标识</Label>
          <Input
            id='field-key'
            value={form.key}
            onChange={(e) => setForm({ ...form, key: e.target.value })}
            required
            maxLength={40}
            pattern='[a-z][a-z0-9_]*'
            title='小写字母开头，只能包含小写字母、数字和下划线'
            disabled={!!field}
            className='font-mono'
          />
        </div>
        <div className='grid gap-2'>
          <Label htmlFor='field-type'>类型</Label>
          <Select value={form.type} onValueChange={(v) => setForm({ ...form, type: v as CrmFieldType })} disabled={!!field}>
            <SelectTrigger id='field-type' className='w-full'>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(Object.keys(crmFieldType) as CrmFieldType[]).map((type) => (
                <SelectItem key={type} value={type}>
                  {crmFieldType[type].label}
                  <span className='text-muted-foreground text-xs'>{crmFieldType[type].hint}</span>
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        {form.type !== 'checkbox' ? (
          <div className='flex items-center justify-between gap-3 self-end rounded-md border px-3 py-2'>
            <Label htmlFor='field-required'>新建时必填</Label>
            <Switch id='field-required' checked={form.required} onCheckedChange={(required) => setForm({ ...form, required })} />
          </div>
        ) : null}
      </div>
      {hasOptions ? (
        <div className='grid gap-2'>
          <Label htmlFor='field-options'>选项</Label>
          <Textarea id='field-options' rows={4} value={form.options} onChange={(e) => setForm({ ...form, options: e.target.value })} placeholder={'每行一个，例如：\n华东\n华南'} />
          {field ? <p className='text-muted-foreground text-xs'>删除选项会同时清除记录里已经选了它的值。</p> : null}
        </div>
      ) : null}
      <div className='grid gap-2'>
        <Label htmlFor='field-description'>说明</Label>
        <Input id='field-description' value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} maxLength={500} placeholder='填写时显示的提示（可选）' />
      </div>
      <div className='flex justify-end gap-2'>
        <Button type='button' variant='outline' onClick={onDone}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending}>
          保存
        </Button>
      </div>
    </form>
  );
}
