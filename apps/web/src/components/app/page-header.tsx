'use client';

import { useDocumentTitle } from '@/hooks/use-document-title';

/**
 * 页面标题区。同时设置浏览器标签页标题（默认与标题相同，documentTitle 可以写得更具体，例如区分同一模块下的几个页面）。
 */
export function PageHeader({
  title,
  description,
  actions,
  documentTitle,
}: {
  title: string;
  description?: string;
  actions?: React.ReactNode;
  documentTitle?: string;
}) {
  useDocumentTitle(documentTitle ?? title);
  return (
    <div className='flex flex-wrap items-start justify-between gap-4'>
      <div className='space-y-1'>
        <h1 className='text-2xl font-semibold tracking-tight'>{title}</h1>
        {description ? <p className='text-muted-foreground text-sm'>{description}</p> : null}
      </div>
      {actions ? <div className='flex flex-wrap items-center gap-2'>{actions}</div> : null}
    </div>
  );
}
