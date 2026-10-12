'use client';

import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { TenantSecretDto } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { EmptyState } from '@/components/app/empty-state';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { formatTime } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { fieldError, fieldErrorsOf, FieldMessage } from '@/components/app/field-errors';

const reference = (name: string) => `{{ secrets.${name} }}`;

/**
 * 空间密钥：加密保存的令牌，只能在外部服务和工作流 HTTP 步骤的请求头里按名称引用，保存后不再显示明文。
 */
export function SecretsTab() {
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const { can } = useSession();
  const manage = can('integrations.manage');
  const secrets = useQuery({ queryKey: queryKeys.secrets, queryFn: ({ signal }) => api.integrations.secrets(signal) });
  const [editing, setEditing] = useState<TenantSecretDto | 'new' | null>(null);
  const remove = useMutation({
    mutationFn: (id: string) => api.integrations.deleteSecret(id),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.secrets });
      toast.success('密钥已删除');
    },
  });

  return (
    <div className='space-y-4'>
      <div className='flex flex-wrap items-start justify-between gap-4'>
        <p className='text-muted-foreground max-w-2xl text-sm'>
          把 API 令牌等敏感值加密保存在这里，再在外部服务或工作流 HTTP 步骤的请求头里写 <code className='font-mono whitespace-nowrap'>{reference('名称')}</code> 引用。
          保存后任何人都看不到明文，只能更换。引用密钥的人需要有「在工作流 HTTP 请求和外部服务的请求头里引用密钥」的权限：能引用就能把它发往任意地址。
        </p>
        {manage ? (
          <Button onClick={() => setEditing('new')}>
            <Icons.add />
            添加密钥
          </Button>
        ) : null}
      </div>
      {secrets.data?.length ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>名称</TableHead>
              <TableHead>引用写法</TableHead>
              <TableHead>说明</TableHead>
              <TableHead>最近更新</TableHead>
              <TableHead />
            </TableRow>
          </TableHeader>
          <TableBody>
            {secrets.data.map((secret) => (
              <TableRow key={secret.id}>
                <TableCell className='font-mono text-sm font-medium'>{secret.name}</TableCell>
                <TableCell>
                  <CopyReference name={secret.name} />
                </TableCell>
                <TableCell className='text-muted-foreground max-w-xs truncate text-sm'>{secret.description || '—'}</TableCell>
                <TableCell className='text-muted-foreground text-sm'>
                  {secret.updatedBy.name} · {formatTime(secret.updatedAt)}
                </TableCell>
                <TableCell className='text-right whitespace-nowrap'>
                  {manage ? (
                    <>
                      <Button size='sm' variant='ghost' onClick={() => setEditing(secret)}>
                        更换
                      </Button>
                      <Button
                        size='sm'
                        variant='ghost'
                        aria-label={`删除 ${secret.name}`}
                        onClick={() =>
                          void confirm({ title: `删除密钥 ${secret.name}？`, description: '删除后无法恢复。', confirmLabel: '删除' }).then(
                            (ok) => ok && remove.mutate(secret.id),
                          )
                        }
                      >
                        <Icons.trash />
                      </Button>
                    </>
                  ) : null}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : secrets.isPending ? (
        <Skeleton className='h-40 w-full' />
      ) : secrets.isError ? (
        <ErrorState query={secrets} />
      ) : (
        <EmptyState title='还没有密钥' description='添加一个密钥（例如 GITHUB_TOKEN），就能在接入外部服务时引用，不用把令牌明文写进配置。' />
      )}
      <SecretDialog editing={editing} onClose={() => setEditing(null)} />
    </div>
  );
}

function CopyReference({ name }: { name: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type='button'
      className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1.5 font-mono text-xs'
      title='复制引用写法'
      onClick={() => {
        void navigator.clipboard.writeText(reference(name)).then(() => {
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        });
      }}
    >
      {reference(name)}
      {copied ? <Icons.check className='size-3.5' /> : <Icons.copy className='size-3.5' />}
    </button>
  );
}

function SecretDialog({ editing, onClose }: { editing: TenantSecretDto | 'new' | null; onClose: () => void }) {
  return (
    <Dialog open={editing !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent>{editing ? <SecretForm secret={editing === 'new' ? undefined : editing} onClose={onClose} /> : null}</DialogContent>
    </Dialog>
  );
}

function SecretForm({ secret, onClose }: { secret?: TenantSecretDto; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(secret?.name ?? '');
  const [value, setValue] = useState('');
  const [description, setDescription] = useState(secret?.description ?? '');
  const save = useMutation({
    mutationFn: () =>
      secret
        ? api.integrations.updateSecret(secret.id, { value: value || undefined, description: description.trim() })
        : api.integrations.createSecret({ name, value, description: description.trim() }),
    onSuccess: async (saved) => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.secrets });
      toast.success(secret ? (value ? `已更换 ${saved.name} 的值` : '说明已保存') : `密钥 ${saved.name} 已保存`);
      onClose();
    },
  });
  const errors = fieldErrorsOf(save.error);

  return (
    <form
      className='grid gap-4'
      autoComplete='off'
      onSubmit={(e) => {
        e.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{secret ? `更换 ${secret.name}` : '添加密钥'}</DialogTitle>
        <DialogDescription>
          {secret ? '填入新的值会替换原来的值，引用它的服务和工作流下次调用时生效。只改说明时把值留空。' : '值会加密保存，保存后不再显示。'}
        </DialogDescription>
      </DialogHeader>
      {secret ? null : (
        <div className='grid gap-2'>
          <Label htmlFor='secret-name'>名称</Label>
          <Input
            id='secret-name'
            className='font-mono'
            value={name}
            onChange={(e) => setName(e.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, '_'))}
            required
            maxLength={64}
            placeholder='GITHUB_TOKEN'
          />
          <FieldMessage message={fieldError(errors, 'name')} />
          {fieldError(errors, 'name') ? null : (
            <p className='text-muted-foreground text-xs'>
              引用时写 <code className='font-mono whitespace-nowrap'>{reference(name || '名称')}</code>。
            </p>
          )}
        </div>
      )}
      <div className='grid gap-2'>
        <Label htmlFor='secret-value'>{secret ? '新的值' : '值'}</Label>
        <Input
          id='secret-value'
          className='font-mono'
          type='password'
          autoComplete='new-password'
          value={value}
          onChange={(e) => setValue(e.target.value)}
          required={!secret}
          maxLength={8000}
          placeholder={secret ? '留空则不更换' : '粘贴令牌'}
        />
        <FieldMessage message={fieldError(errors, 'value')} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='secret-description'>说明</Label>
        <Textarea id='secret-description' value={description} onChange={(e) => setDescription(e.target.value)} rows={2} maxLength={500} placeholder='用途、来源、到期时间等（可选）' />
        <FieldMessage message={fieldError(errors, 'description')} />
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
