'use client';

import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { TenantSummaryDto } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { api } from '@/lib/api';
import { localTimeZone } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

export function TenantTab() {
  const { session } = useSession();
  if (!session) {
    return null;
  }

  // 空间信息被别人改过（实时通道刷新了会话）时，表单跟着重置。
  return <TenantForm key={`${session.tenant.id}:${session.tenant.name}:${session.tenant.timeZone}`} tenant={session.tenant} />;
}

function TenantForm({ tenant }: { tenant: TenantSummaryDto }) {
  const { can } = useSession();
  const queryClient = useQueryClient();
  const manage = can('platform.settings.manage');
  const [name, setName] = useState(tenant.name);
  const [timeZone, setTimeZone] = useState(tenant.timeZone);
  const zones = useMemo(() => {
    try {
      return Intl.supportedValuesOf('timeZone');
    } catch {
      return [];
    }
  }, []);
  const local = localTimeZone();
  const save = useMutation({
    mutationFn: () => api.tenant.updateSettings({ name: name.trim(), timeZone: timeZone.trim() }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: queryKeys.session });
      await queryClient.invalidateQueries({ queryKey: ['analytics'] });
      toast.success('空间设置已保存');
    },
  });
  const changed = name.trim() !== tenant.name || timeZone.trim() !== tenant.timeZone;

  return (
    <Card className='max-w-2xl'>
      <CardHeader>
        <CardTitle>空间</CardTitle>
        <CardDescription>时区决定「今天」「本月」从几点算起：经营分析按它分天统计，智能体和对话助手也按它理解日期。</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className='grid gap-4'
          onSubmit={(event) => {
            event.preventDefault();
            save.mutate();
          }}
        >
          <div className='grid gap-2'>
            <Label htmlFor='tenant-name'>空间名称</Label>
            <Input id='tenant-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} disabled={!manage} />
            <p className='text-muted-foreground text-xs'>标识：{tenant.slug}</p>
          </div>
          <div className='grid gap-2'>
            <Label htmlFor='tenant-time-zone'>时区</Label>
            <div className='flex flex-wrap gap-2'>
              <Input
                id='tenant-time-zone'
                className='min-w-56 flex-1'
                list='tenant-time-zones'
                value={timeZone}
                onChange={(e) => setTimeZone(e.target.value)}
                required
                maxLength={64}
                disabled={!manage}
                placeholder='例如 Asia/Shanghai'
              />
              {manage && local && local !== timeZone ? (
                <Button type='button' variant='outline' onClick={() => setTimeZone(local)}>
                  使用本机时区（{local}）
                </Button>
              ) : null}
            </div>
            <datalist id='tenant-time-zones'>
              {zones.map((zone) => (
                <option key={zone} value={zone} />
              ))}
            </datalist>
            <LocalTime timeZone={timeZone} />
          </div>
          {manage ? (
            <div>
              <Button type='submit' disabled={!changed || save.isPending || !name.trim() || !timeZone.trim()}>
                保存
              </Button>
            </div>
          ) : (
            <p className='text-muted-foreground text-xs'>只有拥有「修改空间名称与时区」权限的成员可以修改。</p>
          )}
        </form>
      </CardContent>
    </Card>
  );
}

/** 该时区的当前时间，帮助确认选对了。 */
function LocalTime({ timeZone }: { timeZone: string }) {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(timer);
  }, []);

  let text: string;
  try {
    text = `当地现在是 ${now.toLocaleString('zh-CN', {
      timeZone,
      hour12: false,
      year: 'numeric',
      month: 'long',
      day: 'numeric',
      weekday: 'short',
      hour: '2-digit',
      minute: '2-digit',
    })}`;
  } catch {
    text = '无法识别这个时区，请从列表里选择，例如 Asia/Shanghai。';
  }

  return <p className='text-muted-foreground text-xs'>{text}</p>;
}
