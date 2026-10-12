'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { RoleDto, SaveRoleRequest } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { FeatureChecklist, useFeatureCatalog } from '@/components/app/feature-checklist';
import { ErrorState } from '@/components/app/query-state';
import { RawKeyTooltip } from '@/components/app/raw-key';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

export function RolesTab() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const manage = can('platform.roles.manage');
  const roles = useQuery({ queryKey: queryKeys.roles, queryFn: ({ signal }) => api.roles.list(signal) });
  const { label } = useFeatureCatalog();
  const [editing, setEditing] = useState<RoleDto | 'new' | null>(null);
  const remove = useMutation({
    mutationFn: (id: string) => api.roles.delete(id),
    meta: { successMessage: '角色已删除' },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: queryKeys.roles }),
  });

  return (
    <div className='space-y-4'>
      {manage ? (
        <div className='flex justify-end'>
          <Button onClick={() => setEditing('new')}>
            <Icons.add />
            新建角色
          </Button>
        </div>
      ) : null}
      {roles.isPending ? (
        <div className='grid gap-4 md:grid-cols-2'>
          <Skeleton className='h-36' />
          <Skeleton className='h-36' />
        </div>
      ) : null}
      {roles.isError && !roles.data ? <ErrorState query={roles} /> : null}
      <div className='grid gap-4 md:grid-cols-2'>
        {roles.data?.map((role) => (
          <Card key={role.id}>
            <CardHeader className='flex flex-row items-start justify-between gap-2'>
              <div>
                <CardTitle className='flex items-center gap-2'>
                  {role.name}
                  {role.isSystem ? <Badge variant='outline'>内置</Badge> : null}
                </CardTitle>
                <p className='text-muted-foreground font-mono text-xs'>{role.key}</p>
              </div>
              {manage && !role.isSystem ? (
                <div className='flex gap-1'>
                  <Button size='sm' variant='ghost' onClick={() => setEditing(role)} aria-label={`编辑角色「${role.name}」`}>
                    <Icons.edit />
                  </Button>
                  <Button
                    size='sm'
                    variant='ghost'
                    disabled={remove.isPending}
                    onClick={() => void confirm({ title: `删除角色「${role.name}」？`, confirmLabel: '删除' }).then((ok) => ok && remove.mutate(role.id))}
                    aria-label={`删除角色「${role.name}」`}
                  >
                    <Icons.trash />
                  </Button>
                </div>
              ) : null}
            </CardHeader>
            <CardContent className='space-y-2 text-sm'>
              {role.description ? <p className='text-muted-foreground'>{role.description}</p> : null}
              <div className='flex flex-wrap gap-1'>
                {role.features.map((f) => (
                  <RawKeyTooltip key={f} value={f}>
                    <Badge variant='secondary'>{label(f)}</Badge>
                  </RawKeyTooltip>
                ))}
              </div>
            </CardContent>
          </Card>
        ))}
      </div>
      {editing ? <RoleDialog role={editing === 'new' ? null : editing} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function RoleDialog({ role, onClose }: { role: RoleDto | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [form, setForm] = useState<SaveRoleRequest>(
    role ? { key: role.key, name: role.name, description: role.description, features: role.features } : { key: '', name: '', description: '', features: [] },
  );
  const save = useMutation({
    mutationFn: () => (role ? api.roles.update(role.id, form) : api.roles.create(form)),
    meta: { successMessage: role ? '角色已保存' : '角色已创建' },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.roles });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className='max-h-[90svh] overflow-y-auto sm:max-w-2xl'>
        <DialogHeader>
          <DialogTitle>{role ? '编辑角色' : '新建角色'}</DialogTitle>
        </DialogHeader>
        <form
          className='grid gap-4'
          onSubmit={(e) => {
            e.preventDefault();
            save.mutate();
          }}
        >
          <div className='grid gap-4 sm:grid-cols-2'>
            <div className='grid gap-2'>
              <Label htmlFor='role-name'>名称</Label>
              <Input id='role-name' required value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
            </div>
            <div className='grid gap-2'>
              <Label htmlFor='role-key'>标识</Label>
              <Input
                id='role-key'
                required
                pattern='[a-z][a-z0-9_\-]*'
                value={form.key}
                onChange={(e) => setForm({ ...form, key: e.target.value })}
                placeholder='例如 sales'
              />
            </div>
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='role-description'>说明</Label>
            <Textarea id='role-description' rows={2} value={form.description ?? ''} onChange={(e) => setForm({ ...form, description: e.target.value })} />
          </div>
          <div className='grid gap-3'>
            <Label>权限</Label>
            <FeatureChecklist value={form.features} onChange={(features) => setForm({ ...form, features })} />
          </div>
          <DialogFooter>
            <Button type='submit' disabled={save.isPending}>
              保存
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
