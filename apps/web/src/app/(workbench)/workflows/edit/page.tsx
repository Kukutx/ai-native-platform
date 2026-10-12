'use client';

import { Suspense } from 'react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { useQuery } from '@tanstack/react-query';
import { DetailState } from '@/components/app/query-state';
import { Icons } from '@/components/icons';
import { Skeleton } from '@/components/ui/skeleton';
import { WorkflowEditor } from '@/features/workflows/workflow-editor';
import { useDocumentTitle } from '@/hooks/use-document-title';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

function EditWorkflow() {
  const id = useSearchParams().get('id');
  const workflow = useQuery({ queryKey: queryKeys.workflow(id ?? ''), queryFn: ({ signal }) => api.workflows.get(id!, signal), enabled: !!id });
  const heading = id ? (workflow.data ? `编辑「${workflow.data.name}」` : '编辑工作流') : '新建工作流';
  useDocumentTitle(heading);

  return (
    <div className='mx-auto max-w-4xl space-y-6 p-6'>
      <Link href={id ? `/workflows/view?id=${id}` : '/workflows'} className='text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-sm'>
        <Icons.chevronLeft className='size-4' />
        {id ? '返回工作流' : '自动化'}
      </Link>
      <h1 className='text-2xl font-semibold tracking-tight'>{heading}</h1>
      {id && !workflow.data ? (
        workflow.isError ? (
          <DetailState id={id} query={workflow} subject='工作流' back={{ href: '/workflows', label: '自动化' }} />
        ) : (
          <Skeleton className='h-96 w-full' />
        )
      ) : (
        // 载入后再挂载编辑器，草稿只在初次渲染时从数据生成。
        <WorkflowEditor key={workflow.data?.id ?? 'new'} workflow={workflow.data} />
      )}
    </div>
  );
}

export default function EditWorkflowPage() {
  return (
    <Suspense>
      <EditWorkflow />
    </Suspense>
  );
}
