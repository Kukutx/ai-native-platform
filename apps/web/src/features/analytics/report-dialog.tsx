'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { AnalyticsReportDto, SaveAnalyticsReportRequest } from '@ainative/api-client';
import { EditConflictAlert, isEditConflict, useEditVersion } from '@/components/app/edit-conflict';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/**
 * 保存报表：report 不为空时更新它，否则新建。
 * baseVersion 是页面开始编辑时报表的版本，提交它防止悄悄覆盖别人在这期间的修改；冲突时可以载入最新或仍然覆盖。
 */
export function ReportDialog({
  open,
  report,
  baseVersion,
  config,
  defaultName,
  onOpenChange,
  onSaved,
  onLoadLatest,
}: {
  open: boolean;
  report: AnalyticsReportDto | null;
  baseVersion?: string;
  config: Omit<SaveAnalyticsReportRequest, 'name' | 'description' | 'expectedUpdatedAt'>;
  defaultName: string;
  onOpenChange: (open: boolean) => void;
  onSaved: (report: AnalyticsReportDto) => void;
  /** 冲突时选择「载入最新」：最新内容已写入查询缓存，由页面用它重新开始编辑。 */
  onLoadLatest: () => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        {open ? (
          <ReportForm
            key={report?.id ?? 'new'}
            report={report}
            baseVersion={baseVersion}
            config={config}
            defaultName={defaultName}
            onClose={() => onOpenChange(false)}
            onSaved={onSaved}
            onLoadLatest={onLoadLatest}
          />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function ReportForm({
  report,
  baseVersion,
  config,
  defaultName,
  onClose,
  onSaved,
  onLoadLatest,
}: {
  report: AnalyticsReportDto | null;
  baseVersion?: string;
  config: Omit<SaveAnalyticsReportRequest, 'name' | 'description' | 'expectedUpdatedAt'>;
  defaultName: string;
  onClose: () => void;
  onSaved: (report: AnalyticsReportDto) => void;
  onLoadLatest: () => void;
}) {
  const queryClient = useQueryClient();
  const edit = useEditVersion(
    report,
    () => {
      const id = report!.id;
      return queryClient.fetchQuery({ queryKey: queryKeys.analyticsReport(id), queryFn: ({ signal }) => api.analytics.getReport(id, signal), staleTime: 0 });
    },
    baseVersion,
  );
  const [name, setName] = useState(report?.name ?? defaultName);
  const [description, setDescription] = useState(report?.description ?? '');
  const save = useMutation({
    mutationFn: () => {
      const request: SaveAnalyticsReportRequest = { ...config, name, description };
      return report ? api.analytics.updateReport(report.id, { ...request, expectedUpdatedAt: edit.expected() }) : api.analytics.createReport(request);
    },
    meta: { handledError: isEditConflict },
    onSuccess: (saved) => {
      // 先让页面记下新版本，再更新缓存，避免页面短暂把自己的保存当成别人的修改。
      onSaved(saved);
      queryClient.setQueryData(queryKeys.analyticsReport(saved.id), saved);
      void queryClient.invalidateQueries({ queryKey: queryKeys.analyticsReports });
      onClose();
    },
    onError: edit.onError,
  });

  return (
    <form
      className='grid gap-4'
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <DialogHeader>
        <DialogTitle>{report ? '保存报表' : '保存为报表'}</DialogTitle>
        <DialogDescription>报表保存的是指标、拆分方式和区间设置，每次打开都按最新数据计算，空间里有查看权限的人都能看到。</DialogDescription>
      </DialogHeader>
      <div className='grid gap-2'>
        <Label htmlFor='report-name'>名称</Label>
        <Input id='report-name' value={name} onChange={(e) => setName(e.target.value)} required maxLength={100} />
      </div>
      <div className='grid gap-2'>
        <Label htmlFor='report-description'>说明</Label>
        <Textarea
          id='report-description'
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={3}
          maxLength={1000}
          placeholder='这张报表回答什么问题（选填）'
        />
      </div>
      <EditConflictAlert edit={edit} subject='报表' onLoadLatest={onLoadLatest} onOverwrite={() => save.mutate()} disabled={save.isPending} />
      <DialogFooter>
        <Button type='button' variant='outline' onClick={onClose}>
          取消
        </Button>
        <Button type='submit' disabled={save.isPending || !name.trim()}>
          保存
        </Button>
      </DialogFooter>
    </form>
  );
}
