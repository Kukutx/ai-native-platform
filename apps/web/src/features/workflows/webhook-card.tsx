'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { WorkflowDto } from '@ainative/api-client';
import { useConfirm } from '@/components/app/confirm-provider';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { api } from '@/lib/api';
import { absoluteApiUrl } from '@/lib/config';
import { queryKeys } from '@/lib/query-keys';

/** Webhook 触发的地址、调用示例和重新生成。地址只对能管理工作流的成员返回。 */
export function WebhookCard({ workflow, canManage }: { workflow: WorkflowDto; canManage: boolean }) {
  const queryClient = useQueryClient();
  const confirm = useConfirm();
  const regenerate = useMutation({
    mutationFn: () => api.workflows.regenerateWebhook(workflow.id),
    onSuccess: async (saved) => {
      queryClient.setQueryData(queryKeys.workflow(workflow.id), saved);
      await queryClient.invalidateQueries({ queryKey: queryKeys.workflows });
      toast.success('已换成新地址，旧地址不再可用');
    },
  });

  const url = workflow.webhookToken ? absoluteApiUrl(`/api/hooks/workflows/${workflow.webhookToken}`) : null;
  const example = url ? `curl -X POST '${url}' \\\n  -H 'Content-Type: application/json' \\\n  -d '{"title": "测试"}'` : null;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Webhook 地址</CardTitle>
        <CardDescription>
          外部系统用 POST 调用这个地址即可触发工作流，不需要登录：拿到地址就能触发，请像密码一样保管。
          {workflow.isEnabled ? null : ' 工作流停用期间调用会返回 404。'}
        </CardDescription>
      </CardHeader>
      <CardContent className='grid gap-4'>
        {url && example ? (
          <>
            <CopyBlock value={url} label='复制地址' />
            <div className='grid gap-1'>
              <p className='text-muted-foreground text-xs'>调用示例。请求体在步骤里用 trigger.body 引用，查询参数是 trigger.query，请求头是 trigger.headers（名称小写）。成功时返回 202。</p>
              <CopyBlock value={example} label='复制示例' />
            </div>
            <div>
              <Button
                variant='outline'
                size='sm'
                disabled={regenerate.isPending}
                onClick={() =>
                  void confirm({
                    title: '换一个新地址？',
                    description: '旧地址立即失效，已经配置了旧地址的外部系统需要更新。',
                    confirmLabel: '换新地址',
                  }).then((ok) => ok && regenerate.mutate())
                }
              >
                <Icons.refresh className='size-4' />
                重新生成地址
              </Button>
            </div>
          </>
        ) : (
          <p className='text-muted-foreground text-sm'>{canManage ? '地址生成中，刷新页面后再看。' : '地址只对能管理工作流的成员显示。'}</p>
        )}
      </CardContent>
    </Card>
  );
}

function CopyBlock({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className='bg-muted relative rounded-md'>
      <pre className='overflow-x-auto p-3 pr-12 font-mono text-xs break-all whitespace-pre-wrap'>{value}</pre>
      <Button
        type='button'
        size='icon'
        variant='ghost'
        className='absolute top-1.5 right-1.5 size-8'
        aria-label={label}
        onClick={() => {
          void navigator.clipboard.writeText(value).then(() => {
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          });
        }}
      >
        {copied ? <Icons.check className='size-4' /> : <Icons.copy className='size-4' />}
      </Button>
    </div>
  );
}
