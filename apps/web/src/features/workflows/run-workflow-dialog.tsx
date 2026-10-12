'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation } from '@tanstack/react-query';
import type { WorkflowDto } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { api } from '@/lib/api';

/** 步骤里引用了 input.xxx 时才需要填参数，否则直接运行。 */
export function usesInput(workflow: WorkflowDto) {
  return JSON.stringify(workflow.steps).includes('input.');
}

export function RunWorkflowDialog({
  workflow,
  open,
  onOpenChange,
}: {
  workflow: WorkflowDto;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}) {
  const router = useRouter();
  const [text, setText] = useState('{\n  \n}');
  const [error, setError] = useState<string | null>(null);
  const start = useMutation({
    mutationFn: (input: unknown) => api.workflows.start(workflow.id, { input }),
    onSuccess: (run) => {
      onOpenChange(false);
      router.push(`/workflows/run?id=${run.id}`);
    },
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className='sm:max-w-lg'>
        <DialogHeader>
          <DialogTitle>运行「{workflow.name}」</DialogTitle>
          <DialogDescription>运行参数是一个 JSON 对象，步骤里用 input.字段名 引用。</DialogDescription>
        </DialogHeader>
        <form
          className='grid gap-4'
          onSubmit={(event) => {
            event.preventDefault();
            let input: unknown;
            try {
              input = text.trim() ? JSON.parse(text) : {};
            } catch {
              setError('不是合法的 JSON。');
              return;
            }

            if (typeof input !== 'object' || input === null || Array.isArray(input)) {
              setError('运行参数必须是 JSON 对象，例如 {"customer": "星火科技"}。');
              return;
            }

            setError(null);
            start.mutate(input);
          }}
        >
          <div className='grid gap-2'>
            <Label htmlFor='run-input'>运行参数</Label>
            <Textarea
              id='run-input'
              className='font-mono text-sm'
              rows={8}
              value={text}
              onChange={(e) => setText(e.target.value)}
              aria-invalid={!!error}
            />
            {error ? <p className='text-destructive text-sm'>{error}</p> : null}
          </div>
          <DialogFooter>
            <Button type='submit' disabled={start.isPending}>
              运行
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

/**
 * 运行按钮：需要参数时弹窗填写，否则直接运行并打开运行详情。
 */
export function RunWorkflowButton({ workflow, size = 'sm', variant = 'outline' }: { workflow: WorkflowDto; size?: 'sm' | 'default'; variant?: 'outline' | 'default' }) {
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const start = useMutation({
    mutationFn: () => api.workflows.start(workflow.id, {}),
    onSuccess: (run) => router.push(`/workflows/run?id=${run.id}`),
  });

  return (
    <>
      <Button size={size} variant={variant} disabled={start.isPending} onClick={() => (usesInput(workflow) ? setOpen(true) : start.mutate())}>
        运行
      </Button>
      {open ? <RunWorkflowDialog workflow={workflow} open={open} onOpenChange={setOpen} /> : null}
    </>
  );
}
