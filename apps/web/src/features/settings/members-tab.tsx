'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { AddMemberRequest, MemberDto, RoleDto } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { useSession } from '@/components/app/session-provider';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { RoleChecklist } from './role-checklist';

export function MembersTab() {
  const { can, session } = useSession();
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const members = useQuery({ queryKey: queryKeys.members, queryFn: ({ signal }) => api.members.list(signal) });
  const roles = useQuery({ queryKey: queryKeys.roles, queryFn: ({ signal }) => api.roles.list(signal) });
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<MemberDto | null>(null);
  const manage = can('platform.members.manage');
  const refresh = () => queryClient.invalidateQueries({ queryKey: queryKeys.members });
  const remove = useMutation({ mutationFn: (id: string) => api.members.remove(id), onSuccess: refresh });
  const toggle = useMutation({
    mutationFn: (m: MemberDto) =>
      api.members.update(m.membershipId, {
        roleIds: m.roleIds,
        organizationId: m.organizationId,
        status: m.status === 'active' ? 'suspended' : 'active',
      }),
    onSuccess: refresh,
  });
  const roleNames = new Map(roles.data?.map((r) => [r.id, r.name]));

  return (
    <div className='space-y-4'>
      {manage ? (
        <div className='flex justify-end'>
          <Button onClick={() => setAdding(true)}>
            <Icons.add />
            添加成员
          </Button>
        </div>
      ) : null}
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>成员</TableHead>
            <TableHead>角色</TableHead>
            <TableHead>状态</TableHead>
            <TableHead>加入时间</TableHead>
            <TableHead />
          </TableRow>
        </TableHeader>
        <TableBody>
          {members.data?.map((m) => (
            <TableRow key={m.membershipId}>
              <TableCell>
                <div className='font-medium'>
                  {m.displayName}
                  {m.isOwner ? <Badge className='ml-2'>所有者</Badge> : null}
                </div>
                <div className='text-muted-foreground text-xs'>{m.email}</div>
              </TableCell>
              <TableCell className='space-x-1'>
                {m.roleIds.map((id) => (
                  <Badge key={id} variant='secondary'>
                    {roleNames.get(id) ?? '角色'}
                  </Badge>
                ))}
              </TableCell>
              <TableCell>{m.status === 'active' ? '正常' : '已停用'}</TableCell>
              <TableCell className='text-muted-foreground'>{formatTime(m.joinedAt)}</TableCell>
              <TableCell className='text-right'>
                {manage && !m.isOwner && m.userId !== session?.user.id ? (
                  <div className='flex justify-end gap-1'>
                    <Button size='sm' variant='ghost' onClick={() => setEditing(m)}>
                      角色
                    </Button>
                    <Button size='sm' variant='ghost' onClick={() => toggle.mutate(m)}>
                      {m.status === 'active' ? '停用' : '启用'}
                    </Button>
                    <Button
                      size='sm'
                      variant='ghost'
                      disabled={remove.isPending}
                      aria-label={`移除 ${m.displayName}`}
                      onClick={() => void confirm({ title: `移除 ${m.displayName}？`, confirmLabel: '移除' }).then((ok) => ok && remove.mutate(m.membershipId))}
                    >
                      <Icons.trash />
                    </Button>
                  </div>
                ) : null}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      <AddMemberDialog open={adding} onOpenChange={setAdding} roles={roles.data ?? []} />
      {editing ? <EditRolesDialog member={editing} roles={roles.data ?? []} onClose={() => setEditing(null)} /> : null}
    </div>
  );
}

function AddMemberDialog({ open, onOpenChange, roles }: { open: boolean; onOpenChange: (open: boolean) => void; roles: RoleDto[] }) {
  const queryClient = useQueryClient();
  const empty: AddMemberRequest = { email: '', displayName: '', password: '', roleIds: [] };
  const [form, setForm] = useState<AddMemberRequest>(empty);
  const add = useMutation({
    mutationFn: () => api.members.add({ ...form, password: form.password || undefined }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.members });
      setForm(empty);
      onOpenChange(false);
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>添加成员</DialogTitle>
        </DialogHeader>
        <form
          className='grid gap-4'
          onSubmit={(e) => {
            e.preventDefault();
            add.mutate();
          }}
        >
          <div className='grid gap-2'>
            <Label htmlFor='member-email'>邮箱</Label>
            <Input id='member-email' type='email' required value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='member-name'>显示名</Label>
            <Input id='member-name' required value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='member-password'>初始密码</Label>
            <Input
              id='member-password'
              type='password'
              value={form.password ?? ''}
              onChange={(e) => setForm({ ...form, password: e.target.value })}
              placeholder='已有账号的用户可以留空'
            />
          </div>
          <div className='grid gap-2'>
            <Label>角色</Label>
            <RoleChecklist roles={roles} value={form.roleIds} onChange={(roleIds) => setForm({ ...form, roleIds })} />
          </div>
          <DialogFooter>
            <Button type='submit' disabled={add.isPending}>
              添加
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function EditRolesDialog({ member, roles, onClose }: { member: MemberDto; roles: RoleDto[]; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [roleIds, setRoleIds] = useState(member.roleIds);
  const save = useMutation({
    mutationFn: () => api.members.update(member.membershipId, { roleIds, organizationId: member.organizationId, status: member.status }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.members });
      onClose();
    },
  });

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{member.displayName} 的角色</DialogTitle>
        </DialogHeader>
        <RoleChecklist roles={roles} value={roleIds} onChange={setRoleIds} />
        <DialogFooter>
          <Button disabled={save.isPending} onClick={() => save.mutate()}>
            保存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
