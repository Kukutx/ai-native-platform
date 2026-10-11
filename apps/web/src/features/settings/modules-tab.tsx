'use client';

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSession } from '@/components/app/session-provider';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

export function ModulesTab() {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const modules = useQuery({ queryKey: queryKeys.modules, queryFn: ({ signal }) => api.modules.list(signal) });
  const setEnabled = useMutation({
    mutationFn: ({ key, enabled }: { key: string; enabled: boolean }) => api.modules.setEnabled(key, { enabled }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.modules });
      await queryClient.invalidateQueries({ queryKey: queryKeys.session });
    },
  });
  const manage = can('platform.modules.manage');
  const names = new Map(modules.data?.map((m) => [m.key, m.name]));

  return (
    <div className='grid gap-4 md:grid-cols-2'>
      {modules.data?.map((m) => (
        <Card key={m.key}>
          <CardHeader className='flex flex-row items-start justify-between gap-2'>
            <div>
              <CardTitle className='flex items-center gap-2'>
                {m.name}
                {m.isCore ? <Badge variant='outline'>核心</Badge> : null}
              </CardTitle>
              <p className='text-muted-foreground font-mono text-xs'>{m.key}</p>
            </div>
            {manage && !m.isCore ? (
              <Button
                size='sm'
                variant={m.enabled ? 'outline' : 'default'}
                disabled={setEnabled.isPending}
                onClick={() => setEnabled.mutate({ key: m.key, enabled: !m.enabled })}
              >
                {m.enabled ? '停用' : '启用'}
              </Button>
            ) : (
              <Badge variant={m.enabled ? 'secondary' : 'outline'}>{m.enabled ? '已启用' : '未启用'}</Badge>
            )}
          </CardHeader>
          <CardContent className='space-y-2 text-sm'>
            <p className='text-muted-foreground'>{m.description}</p>
            {m.dependsOn.length ? (
              <p className='text-muted-foreground text-xs'>依赖：{m.dependsOn.map((key) => names.get(key) ?? key).join('、')}（需要先启用）</p>
            ) : null}
            <p className='text-muted-foreground text-xs'>{m.features.length} 项权限 · 停用后相关接口、智能体动作一并关闭</p>
          </CardContent>
        </Card>
      ))}
    </div>
  );
}
