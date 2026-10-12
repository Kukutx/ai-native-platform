'use client';

import { useDeferredValue, useState } from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

export interface PickedRecord {
  id: string;
  name: string;
  hint?: string;
}

/**
 * 按名称搜索并选择一个客户或联系人。客户、联系人可能很多，不适合一次性放进下拉框。
 */
export function RecordPicker({
  kind,
  value,
  onChange,
  companyId,
  placeholder,
}: {
  kind: 'company' | 'contact';
  value: PickedRecord | null;
  onChange: (record: PickedRecord | null) => void;
  /** 选联系人时只在这个客户下找。 */
  companyId?: string;
  placeholder?: string;
}) {
  const [search, setSearch] = useState('');
  const [open, setOpen] = useState(false);
  const term = useDeferredValue(search.trim());
  const results = useQuery({
    queryKey: kind === 'company' ? queryKeys.crmCompanies({ search: term, picker: true }) : queryKeys.crmContacts({ search: term, companyId, picker: true }),
    queryFn: async ({ signal }): Promise<PickedRecord[]> => {
      if (kind === 'company') {
        const page = await api.crm.companies.list(term || undefined, undefined, undefined, 1, 8, signal);
        return page.items.map((c) => ({ id: c.id, name: c.name, hint: c.industry || undefined }));
      }

      const page = await api.crm.contacts.list(term || undefined, companyId, undefined, undefined, 1, 8, signal);
      return page.items.map((c) => ({ id: c.id, name: c.name, hint: [c.companyName, c.title].filter(Boolean).join(' · ') || undefined }));
    },
    enabled: open,
    placeholderData: keepPreviousData,
  });

  if (value) {
    return (
      <div className='flex h-9 items-center justify-between rounded-md border px-3 text-sm'>
        <span className='truncate'>
          {value.name}
          {value.hint ? <span className='text-muted-foreground'> · {value.hint}</span> : null}
        </span>
        <Button type='button' size='icon' variant='ghost' className='size-6' onClick={() => onChange(null)} aria-label='清除'>
          <Icons.close className='size-4' />
        </Button>
      </div>
    );
  }

  return (
    <div className='relative'>
      <Input
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        onFocus={() => setOpen(true)}
        // 延迟关闭，让点击结果先生效。
        onBlur={() => setTimeout(() => setOpen(false), 150)}
        placeholder={placeholder ?? (kind === 'company' ? '搜索客户名称' : '搜索联系人')}
      />
      {open ? (
        <div className='bg-popover absolute z-50 mt-1 max-h-64 w-full overflow-y-auto rounded-md border p-1 shadow-md'>
          {results.data?.length ? (
            results.data.map((record) => (
              <button
                key={record.id}
                type='button'
                className='hover:bg-accent flex w-full flex-col items-start rounded-sm px-2 py-1.5 text-left text-sm'
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  onChange(record);
                  setSearch('');
                  setOpen(false);
                }}
              >
                <span>{record.name}</span>
                {record.hint ? <span className='text-muted-foreground text-xs'>{record.hint}</span> : null}
              </button>
            ))
          ) : (
            <p className='text-muted-foreground px-2 py-1.5 text-sm'>{results.isFetching ? '搜索中…' : '没有找到'}</p>
          )}
        </div>
      ) : null}
    </div>
  );
}
