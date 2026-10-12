'use client';

import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { CrmFieldDto, CrmRecordType } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Textarea } from '@/components/ui/textarea';
import { catalogQueries } from '@/lib/catalogs';
import { formatDate } from '@/lib/labels';

/** 表单里的自定义字段值：文本、数字、日期、单选为字符串，多选为数组，是否为布尔。 */
export type CustomValues = Record<string, string | string[] | boolean>;

const NONE = '__none__';

export function useCrmFields(recordType: CrmRecordType) {
  return useQuery(catalogQueries.crmFields(recordType));
}

/** 新建、编辑表单里的自定义字段状态。 */
export function useCustomFieldForm(recordType: CrmRecordType, stored?: Record<string, unknown>) {
  const fields = useCrmFields(recordType);
  const [edited, setEdited] = useState<CustomValues | null>(null);
  const values = edited ?? toFormValues(fields.data ?? [], stored);
  return {
    fields: fields.data ?? [],
    values,
    onChange: setEdited,
    // 字段定义还没加载时不提交，编辑时不会误把已有的值清空。
    request: () => (fields.data ? toRequestValues(fields.data, values) : undefined),
  };
}

/** 记录里存的值转成表单值。 */
export function toFormValues(fields: CrmFieldDto[], stored: Record<string, unknown> | undefined): CustomValues {
  const values: CustomValues = {};
  for (const field of fields) {
    const value = stored?.[field.key];
    if (value === undefined || value === null) {
      continue;
    }

    if (field.type === 'multi_select') {
      values[field.key] = Array.isArray(value) ? value.map(String) : [String(value)];
    } else if (field.type === 'checkbox') {
      values[field.key] = value === true;
    } else {
      values[field.key] = String(value);
    }
  }

  return values;
}

/**
 * 表单值转成接口提交的 customFields：只带当前定义的字段，空值不提交。
 * 编辑时接口按整体替换处理，没带的字段会被清空，所以这里总是带上全部有值的字段。
 */
export function toRequestValues(fields: CrmFieldDto[], values: CustomValues): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const field of fields) {
    const value = values[field.key];
    if (typeof value === 'string' ? value.trim() : Array.isArray(value) ? value.length : value) {
      result[field.key] = typeof value === 'string' ? value.trim() : value;
    }
  }

  return result;
}

/** 表单里的自定义字段输入区；没有定义字段时不显示。 */
export function CustomFieldInputs({ fields, values, onChange }: { fields: CrmFieldDto[]; values: CustomValues; onChange: (values: CustomValues) => void }) {
  if (!fields.length) {
    return null;
  }

  const set = (key: string, value: string | string[] | boolean) => onChange({ ...values, [key]: value });
  return (
    <div className='grid gap-4 border-t pt-4 sm:grid-cols-2'>
      {fields.map((field) => {
        const id = `custom-${field.key}`;
        const wide = field.type === 'text' || field.type === 'multi_select';
        const label = (
          <Label htmlFor={id}>
            {field.label}
            {field.required ? <span className='text-destructive'>*</span> : null}
          </Label>
        );
        const hint = field.description ? <p className='text-muted-foreground text-xs'>{field.description}</p> : null;

        if (field.type === 'checkbox') {
          return (
            <div key={field.key} className='flex items-center justify-between gap-3 rounded-md border px-3 py-2'>
              <div className='grid gap-1'>
                {label}
                {hint}
              </div>
              <Switch id={id} checked={values[field.key] === true} onCheckedChange={(checked) => set(field.key, checked)} />
            </div>
          );
        }

        let input: React.ReactNode;
        if (field.type === 'select') {
          const value = typeof values[field.key] === 'string' && values[field.key] ? (values[field.key] as string) : NONE;
          input = (
            <Select value={value} onValueChange={(v) => set(field.key, v === NONE ? '' : v)}>
              <SelectTrigger id={id} className='w-full'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>未选择</SelectItem>
                {field.options.map((option) => (
                  <SelectItem key={option} value={option}>
                    {option}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          );
        } else if (field.type === 'multi_select') {
          const selected = Array.isArray(values[field.key]) ? (values[field.key] as string[]) : [];
          input = (
            <div id={id} role='group' aria-label={field.label} className='flex flex-wrap gap-2'>
              {field.options.map((option) => {
                const on = selected.includes(option);
                return (
                  <Button
                    key={option}
                    type='button'
                    size='sm'
                    variant={on ? 'default' : 'outline'}
                    aria-pressed={on}
                    onClick={() => set(field.key, on ? selected.filter((o) => o !== option) : [...selected, option])}
                  >
                    {option}
                  </Button>
                );
              })}
            </div>
          );
        } else if (field.type === 'text') {
          input = (
            <Textarea id={id} rows={2} maxLength={2000} value={(values[field.key] as string | undefined) ?? ''} onChange={(e) => set(field.key, e.target.value)} />
          );
        } else {
          input = (
            <Input
              id={id}
              type={field.type === 'date' ? 'date' : 'text'}
              inputMode={field.type === 'number' ? 'decimal' : undefined}
              value={(values[field.key] as string | undefined) ?? ''}
              onChange={(e) => set(field.key, e.target.value)}
            />
          );
        }

        return (
          <div key={field.key} className={wide ? 'grid gap-2 sm:col-span-2' : 'grid gap-2'}>
            {label}
            {input}
            {hint}
          </div>
        );
      })}
    </div>
  );
}

/** 详情页资料卡里的自定义字段；没有定义字段时不显示。 */
export function CustomFieldValues({ recordType, values }: { recordType: CrmRecordType; values: Record<string, unknown> }) {
  const fields = useCrmFields(recordType);
  if (!fields.data?.length) {
    return null;
  }

  return (
    <dl className='grid grid-cols-[minmax(0,6rem)_1fr] gap-x-3 gap-y-2 border-t pt-3'>
      {fields.data.map((field) => (
        <div key={field.key} className='contents'>
          <dt className='text-muted-foreground truncate' title={field.label}>
            {field.label}
          </dt>
          <dd className='min-w-0 break-words whitespace-pre-wrap'>{formatCustomValue(field, values[field.key]) ?? <span className='text-muted-foreground'>—</span>}</dd>
        </div>
      ))}
    </dl>
  );
}

export function formatCustomValue(field: CrmFieldDto, value: unknown): string | null {
  if (value === undefined || value === null || value === '' || (Array.isArray(value) && !value.length)) {
    return field.type === 'checkbox' ? '否' : null;
  }

  switch (field.type) {
    case 'checkbox':
      return value === true ? '是' : '否';
    case 'multi_select':
      return Array.isArray(value) ? value.join('、') : String(value);
    case 'date':
      return formatDate(String(value));
    case 'number':
      return typeof value === 'number' ? value.toLocaleString('zh-CN', { maximumFractionDigits: 6 }) : String(value);
    default:
      return String(value);
  }
}
