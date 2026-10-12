'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ErpWarehouseDto, SaveErpWarehouseRequest } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { api } from '@/lib/api';
import { catalogQueries } from '@/lib/catalogs';

/** 仓库管理：新增、改名、设为默认、删除（有库存或被订单使用的仓库不能删除）。 */
export function WarehousesDialog({ open, onOpenChange, canManage }: { open: boolean; onOpenChange: (open: boolean) => void; canManage: boolean }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        <DialogHeader>
          <DialogTitle>仓库</DialogTitle>
          <DialogDescription>订单不指定仓库时使用默认仓库。</DialogDescription>
        </DialogHeader>
        {open ? <WarehouseList canManage={canManage} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function WarehouseList({ canManage }: { canManage: boolean }) {
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const warehouses = useQuery(catalogQueries.erpWarehouses());
  const [editing, setEditing] = useState<string | null>(null);
  const done = () => queryClient.invalidateQueries({ queryKey: ['erp'] });
  const save = useMutation({
    mutationFn: ({ id, request }: { id?: string; request: SaveErpWarehouseRequest }) =>
      id ? api.erp.warehouses.update(id, request) : api.erp.warehouses.create(request),
    onSuccess: async () => {
      setEditing(null);
      await done();
    },
  });
  const remove = useMutation({ mutationFn: (id: string) => api.erp.warehouses.delete(id), onSuccess: done });

  return (
    <div className='grid gap-3'>
      <ul className='divide-y rounded-lg border'>
        {warehouses.data?.map((w) =>
          editing === w.id ? (
            <li key={w.id} className='p-3'>
              <WarehouseForm
                warehouse={w}
                pending={save.isPending}
                onCancel={() => setEditing(null)}
                onSubmit={(request) => save.mutate({ id: w.id, request: { ...request, isDefault: w.isDefault } })}
              />
            </li>
          ) : (
            <li key={w.id} className='flex items-center gap-2 p-3 text-sm'>
              <div className='min-w-0 flex-1'>
                <p className='flex items-center gap-2 font-medium'>
                  {w.name}
                  {w.isDefault ? <Badge variant='secondary'>默认</Badge> : null}
                </p>
                {w.address ? <p className='text-muted-foreground truncate text-xs'>{w.address}</p> : null}
              </div>
              {canManage ? (
                <>
                  {w.isDefault ? null : (
                    <Button
                      size='sm'
                      variant='ghost'
                      disabled={save.isPending}
                      onClick={() => save.mutate({ id: w.id, request: { name: w.name, address: w.address, isDefault: true } })}
                    >
                      设为默认
                    </Button>
                  )}
                  <Button size='icon' variant='ghost' className='size-8' onClick={() => setEditing(w.id)} aria-label='编辑'>
                    <Icons.edit className='size-4' />
                  </Button>
                  {w.isDefault ? null : (
                    <Button
                      size='icon'
                      variant='ghost'
                      className='size-8'
                      disabled={remove.isPending}
                      onClick={() => void confirm({ title: `删除仓库「${w.name}」？`, confirmLabel: '删除' }).then((ok) => ok && remove.mutate(w.id))}
                      aria-label='删除'
                    >
                      <Icons.trash className='size-4' />
                    </Button>
                  )}
                </>
              ) : null}
            </li>
          ),
        )}
      </ul>
      {canManage ? (
        editing === 'new' ? (
          <div className='rounded-lg border p-3'>
            <WarehouseForm pending={save.isPending} onCancel={() => setEditing(null)} onSubmit={(request) => save.mutate({ request })} />
          </div>
        ) : (
          <Button variant='outline' className='w-fit' onClick={() => setEditing('new')}>
            <Icons.add />
            新增仓库
          </Button>
        )
      ) : null}
    </div>
  );
}

function WarehouseForm({
  warehouse,
  pending,
  onCancel,
  onSubmit,
}: {
  warehouse?: ErpWarehouseDto;
  pending: boolean;
  onCancel: () => void;
  onSubmit: (request: SaveErpWarehouseRequest) => void;
}) {
  const [name, setName] = useState(warehouse?.name ?? '');
  const [address, setAddress] = useState(warehouse?.address ?? '');
  return (
    <form
      className='grid gap-2'
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit({ name, address });
      }}
    >
      <Input value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} placeholder='仓库名称' autoFocus />
      <Input value={address} onChange={(e) => setAddress(e.target.value)} maxLength={500} placeholder='地址（可选）' />
      <div className='flex justify-end gap-2'>
        <Button type='button' size='sm' variant='outline' onClick={onCancel}>
          取消
        </Button>
        <Button type='submit' size='sm' disabled={pending}>
          保存
        </Button>
      </div>
    </form>
  );
}
