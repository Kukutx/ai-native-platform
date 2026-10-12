'use client';

import { useRef, useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { CrmImportDuplicateMode, CrmImportPreviewDto, CrmImportResultDto } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Spinner } from '@/components/ui/spinner';
import { Switch } from '@/components/ui/switch';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { api } from '@/lib/api';
import { crmImportDuplicateMode, crmRecordType } from '@/lib/labels';

type ImportType = 'company' | 'contact';

const ACCEPTED = '.csv,.tsv,.txt,.xlsx,.xlsm';
const MAX_BYTES = 10 * 1024 * 1024;
const SKIP = '__skip__';

/** 从 CSV / Excel 导入客户或联系人：选文件 → 确认每列对应的字段 → 检查 → 导入。 */
export function ImportDialog({ open, recordType, onOpenChange }: { open: boolean; recordType: ImportType; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='max-h-[90vh] overflow-y-auto sm:max-w-3xl'>
        {open ? <Importer recordType={recordType} onClose={() => onOpenChange(false)} /> : null}
      </DialogContent>
    </Dialog>
  );
}

function Importer({ recordType, onClose }: { recordType: ImportType; onClose: () => void }) {
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<CrmImportPreviewDto | null>(null);
  const [mapping, setMapping] = useState<string[]>([]);
  const [duplicates, setDuplicates] = useState<CrmImportDuplicateMode>('skip');
  const [createMissingCompanies, setCreateMissingCompanies] = useState(false);
  const [result, setResult] = useState<CrmImportResultDto | null>(null);
  const label = crmRecordType[recordType];

  const read = useMutation({
    mutationFn: (picked: File) => api.crm.import.preview({ data: picked, fileName: picked.name }, recordType),
    onSuccess: (data, picked) => {
      setFile(picked);
      setPreview(data);
      setMapping(data.columns.map((c) => c.suggestedTarget ?? ''));
      setResult(null);
    },
  });
  const run = useMutation({
    mutationFn: (dryRun: boolean) =>
      api.crm.import.import({ data: file!, fileName: file!.name }, recordType, mapping, duplicates, createMissingCompanies, dryRun),
    onSuccess: async (data) => {
      setResult(data);
      if (!data.dryRun) {
        await queryClient.invalidateQueries({ queryKey: ['crm'] });
      }
    },
  });

  const pick = (picked: File | undefined) => {
    if (!picked) {
      return;
    }

    if (picked.size > MAX_BYTES) {
      read.reset();
      setPreview(null);
      toast.error('文件超过 10 MB，请拆分后再导入。');
      return;
    }

    read.mutate(picked);
  };
  const choose = (index: number, target: string) => {
    // 每个字段只能对应一列：选到别的列已经用的字段时，那一列改为不导入。
    setMapping(mapping.map((current, i) => (i === index ? target : target && current === target ? '' : current)));
    setResult(null);
  };

  const missingRequired = preview?.targets.filter((t) => t.required && !mapping.includes(t.key)) ?? [];
  const nameTarget = preview?.targets.find((t) => t.key === 'name');
  const imported = result && !result.dryRun;

  return (
    <div className='grid gap-4'>
      <DialogHeader>
        <DialogTitle>导入{label}</DialogTitle>
        <DialogDescription>
          支持 .csv 和 .xlsx（Excel 读取第一个有内容的工作表），第一行为表头，最多 5000 行、10 MB。
          {recordType === 'company' ? '按客户名称判断是否已存在。' : '按邮箱判断联系人是否已存在，所属客户按名称匹配。'}
        </DialogDescription>
      </DialogHeader>
      <input ref={input} type='file' accept={ACCEPTED} className='hidden' aria-label='选择要导入的文件' onChange={(e) => pick(e.target.files?.[0])} />
      <div className='flex flex-wrap items-center gap-3'>
        <Button type='button' variant='outline' onClick={() => input.current?.click()} disabled={read.isPending || run.isPending}>
          {read.isPending ? <Spinner /> : <Icons.upload />}
          {read.isPending ? '正在读取文件…' : preview ? '重新选择文件' : '选择文件'}
        </Button>
        {preview ? (
          <span className='text-muted-foreground text-sm'>
            {preview.fileName} · {preview.rowCount} 行数据
          </span>
        ) : null}
      </div>

      {preview && !imported ? (
        <>
          <div className='rounded-md border'>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>表格里的列</TableHead>
                  <TableHead>示例</TableHead>
                  <TableHead className='w-56'>导入到</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {preview.columns.map((column, index) => {
                  const target = preview.targets.find((t) => t.key === mapping[index]);
                  return (
                    <TableRow key={column.index}>
                      <TableCell className='font-medium'>{column.header}</TableCell>
                      <TableCell className='text-muted-foreground max-w-56 truncate text-xs' title={column.samples.join('\n')}>
                        {column.samples.join(' / ') || '（空）'}
                      </TableCell>
                      <TableCell>
                        <Select value={mapping[index] || SKIP} onValueChange={(v) => choose(index, v === SKIP ? '' : v)}>
                          <SelectTrigger className='w-full' aria-label={`「${column.header}」导入到`}>
                            <SelectValue />
                          </SelectTrigger>
                          <SelectContent>
                            <SelectItem value={SKIP}>不导入</SelectItem>
                            {preview.targets.map((t) => (
                              <SelectItem key={t.key} value={t.key}>
                                {t.label}
                                {t.required ? <span className='text-destructive'>*</span> : null}
                              </SelectItem>
                            ))}
                          </SelectContent>
                        </Select>
                        {target?.hint ? <p className='text-muted-foreground mt-1 text-xs'>{target.hint}</p> : null}
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </div>
          <div className='grid gap-4 sm:grid-cols-2'>
            <div className='grid gap-2'>
              <Label htmlFor='import-duplicates'>已经存在的{label}</Label>
              <Select
                value={duplicates}
                onValueChange={(v) => {
                  setDuplicates(v as CrmImportDuplicateMode);
                  setResult(null);
                }}
              >
                <SelectTrigger id='import-duplicates' className='w-full'>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {(Object.keys(crmImportDuplicateMode) as CrmImportDuplicateMode[]).map((mode) => (
                    <SelectItem key={mode} value={mode}>
                      {crmImportDuplicateMode[mode].label}
                      <span className='text-muted-foreground text-xs'>{crmImportDuplicateMode[mode].hint}</span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            {recordType === 'contact' ? (
              <div className='flex items-center justify-between gap-3 self-end rounded-md border px-3 py-2'>
                <Label htmlFor='import-create-companies'>自动新建不存在的客户</Label>
                <Switch
                  id='import-create-companies'
                  checked={createMissingCompanies}
                  onCheckedChange={(checked) => {
                    setCreateMissingCompanies(checked);
                    setResult(null);
                  }}
                />
              </div>
            ) : null}
          </div>
          {missingRequired.length ? (
            <p className='text-destructive text-sm'>
              {mapping.includes('name') ? '' : `请指定哪一列是${nameTarget?.label ?? '名称'}。`}
              {missingRequired.some((t) => t.key !== 'name')
                ? `必填字段${missingRequired
                    .filter((t) => t.key !== 'name')
                    .map((t) => `「${t.label}」`)
                    .join('')}没有对应的列，新建的${label}会因此失败。`
                : null}
            </p>
          ) : null}
        </>
      ) : null}

      {result ? <ImportSummary result={result} label={label} /> : null}

      <DialogFooter>
        {imported ? (
          <Button type='button' onClick={onClose}>
            完成
          </Button>
        ) : (
          <>
            <Button type='button' variant='outline' onClick={onClose}>
              取消
            </Button>
            <Button type='button' variant='outline' disabled={!preview || !mapping.includes('name') || run.isPending} onClick={() => run.mutate(true)}>
              {run.isPending && run.variables ? <Spinner /> : <Icons.checks />}
              检查
            </Button>
            <Button type='button' disabled={!preview || !mapping.includes('name') || run.isPending} onClick={() => run.mutate(false)}>
              {run.isPending && !run.variables ? <Spinner /> : <Icons.download />}
              {result?.dryRun && result.failed ? '导入没有问题的行' : '导入'}
            </Button>
          </>
        )}
      </DialogFooter>
    </div>
  );
}

function ImportSummary({ result, label }: { result: CrmImportResultDto; label: string }) {
  const parts = [
    `新建${label} ${result.created} 个`,
    result.updated ? `更新 ${result.updated} 个` : null,
    result.skipped ? `跳过 ${result.skipped} 行` : null,
    result.companiesCreated ? `自动新建客户 ${result.companiesCreated} 个` : null,
    result.failed ? `${result.failed} 行有问题` : null,
  ].filter(Boolean);

  return (
    <Alert variant={result.failed ? 'destructive' : 'default'}>
      {result.failed ? <Icons.warning /> : <Icons.circleCheck />}
      <AlertTitle className='line-clamp-none'>
        {result.dryRun ? `检查完成，共 ${result.total} 行：将${parts.join('、')}。` : `导入完成，共 ${result.total} 行：已${parts.join('、')}。`}
      </AlertTitle>
      {result.errors.length ? (
        <AlertDescription>
          <ul className='mt-1 max-h-48 w-full space-y-1 overflow-y-auto text-xs'>
            {result.errors.map((e) => (
              <li key={e.row}>
                第 {e.row} 行：{e.message}
              </li>
            ))}
          </ul>
          {result.failed > result.errors.length ? <p className='text-xs'>只列出前 {result.errors.length} 行。</p> : null}
          {result.dryRun ? <p className='text-xs'>有问题的行不会导入，可以修改表格后重新选择文件，也可以直接导入其余的行。</p> : null}
        </AlertDescription>
      ) : null}
    </Alert>
  );
}
