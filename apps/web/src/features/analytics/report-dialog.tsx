'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import type { AnalyticsReportDto, SaveAnalyticsReportRequest } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/**
 * 保存报表：report 不为空时更新它（带上版本号防止覆盖别人的修改），否则新建。
 */
export function ReportDialog({
  open,
  report,
  config,
  defaultName,
  onOpenChange,
  onSaved,
}: {
  open: boolean;
  report: AnalyticsReportDto | null;
  config: Omit<SaveAnalyticsReportRequest, 'name' | 'description' | 'expectedUpdatedAt'>;
  defaultName: string;
  onOpenChange: (open: boolean) => void;
  onSaved: (report: AnalyticsReportDto) => void;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        {open ? (
          <ReportForm
            key={report?.id ?? 'new'}
            report={report}
            config={config}
            defaultName={defaultName}
            onClose={() => onOpenChange(false)}
            onSaved={onSaved}
          />
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

function ReportForm({
  report,
  config,
  defaultName,
  onClose,
  onSaved,
}: {
  report: AnalyticsReportDto | null;
  config: Omit<SaveAnalyticsReportRequest, 'name' | 'description' | 'expectedUpdatedAt'>;
  defaultName: string;
  onClose: () => void;
  onSaved: (report: AnalyticsReportDto) => void;
}) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(report?.name ?? defaultName);
  const [description, setDescription] = useState(report?.description ?? '');
  const save = useMutation({
    mutationFn: () => {
      const request: SaveAnalyticsReportRequest = { ...config, name, description };
      return report ? api.analytics.updateReport(report.id, { ...request, expectedUpdatedAt: report.updatedAt }) : api.analytics.createReport(request);
    },
    onSuccess: async (saved) => {
      queryClient.setQueryData(queryKeys.analyticsReport(saved.id), saved);
      await queryClient.invalidateQueries({ queryKey: queryKeys.analyticsReports });
      onSaved(saved);
      onClose();
    },
    onError: () => {
      if (report) {
        // 多半是别人刚改过：刷新为最新内容，确认后再保存。
        void queryClient.invalidateQueries({ queryKey: queryKeys.analyticsReport(report.id) });
      }
    },
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
