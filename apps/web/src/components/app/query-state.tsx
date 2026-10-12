'use client';

import Link from 'next/link';
import { toProblem } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Button } from '@/components/ui/button';
import { Empty, EmptyContent, EmptyDescription, EmptyHeader, EmptyMedia, EmptyTitle } from '@/components/ui/empty';
import { Skeleton } from '@/components/ui/skeleton';
import { errorMessage } from '@/lib/errors';
import { cn } from '@/lib/utils';

/** ErrorState、DetailState 需要的查询状态（TanStack Query 的 useQuery 结果即可）。 */
export interface QueryStatus {
  error: unknown;
  isError: boolean;
  isFetching: boolean;
  refetch: () => unknown;
}

/** 加载失败：说明原因，并提供重试。列表页请求失败时用它代替“还没有…”的空状态。 */
export function ErrorState({ query, title = '加载失败', className }: { query: QueryStatus; title?: string; className?: string }) {
  return (
    <Empty className={cn('border border-dashed', className)}>
      <EmptyHeader>
        <EmptyMedia variant='icon'>
          <Icons.alertCircle className='text-destructive' />
        </EmptyMedia>
        <EmptyTitle>{title}</EmptyTitle>
        <EmptyDescription>{errorMessage(query.error)}</EmptyDescription>
      </EmptyHeader>
      <EmptyContent>
        <Button variant='outline' disabled={query.isFetching} onClick={() => void query.refetch()}>
          {query.isFetching ? <Icons.spinner className='animate-spin' /> : <Icons.refresh />}
          重试
        </Button>
      </EmptyContent>
    </Empty>
  );
}

/** 卡片、侧栏等小区域里的加载失败：一行说明加重试按钮。 */
export function InlineError({ query, className }: { query: QueryStatus; className?: string }) {
  return (
    <div role='alert' className={cn('text-muted-foreground flex flex-wrap items-center gap-x-2 gap-y-1 text-sm', className)}>
      <Icons.alertCircle className='text-destructive size-4 shrink-0' />
      <span className='min-w-0'>加载失败：{errorMessage(query.error)}</span>
      <Button size='sm' variant='ghost' className='h-7 px-2' disabled={query.isFetching} onClick={() => void query.refetch()}>
        {query.isFetching ? <Icons.spinner className='animate-spin' /> : <Icons.refresh />}
        重试
      </Button>
    </div>
  );
}

/**
 * 详情页拿不到数据时显示的内容：加载中、链接缺少编号或记录不存在、无权查看、其他错误（可重试）。
 * 用法：`if (!detail.data) return <DetailState id={id} query={detail} subject='客户' back={{ href: '/crm/companies', label: '客户' }} />;`
 */
export function DetailState({
  id,
  query,
  subject,
  back,
}: {
  /** 查询参数里的编号；为空时直接按不存在处理（查询不会发出）。 */
  id: string | null | undefined;
  query: QueryStatus;
  /** 记录的名称，例如「客户」「工作流」。 */
  subject: string;
  back: { href: string; label: string };
}) {
  const status = query.isError ? toProblem(query.error).status : undefined;

  if (id && !query.isError) {
    return (
      <div className='space-y-6 p-6' aria-busy='true' aria-label='正在加载'>
        <Skeleton className='h-4 w-20' />
        <Skeleton className='h-8 w-64 max-w-full' />
        <div className='grid gap-6 lg:grid-cols-3'>
          <Skeleton className='h-64 lg:col-span-2' />
          <Skeleton className='h-64' />
        </div>
      </div>
    );
  }

  const backLink = (
    <Button variant='outline' asChild>
      <Link href={back.href}>
        <Icons.chevronLeft />
        返回{back.label}
      </Link>
    </Button>
  );

  if (!id || status === 404) {
    return (
      <StateFrame icon={<Icons.search />} title={`${subject}不存在或已被删除`} description='链接可能已经失效，可以回到列表重新查找。'>
        {backLink}
      </StateFrame>
    );
  }

  if (status === 403) {
    return (
      <StateFrame icon={<Icons.lock />} title={`无权查看这个${subject}`} description='你的角色没有相应权限，或者所属模块没有在当前空间启用。需要时请联系空间管理员。'>
        {backLink}
      </StateFrame>
    );
  }

  return (
    <StateFrame icon={<Icons.alertCircle className='text-destructive' />} title={`${subject}加载失败`} description={errorMessage(query.error)}>
      <div className='flex flex-wrap justify-center gap-2'>
        <Button disabled={query.isFetching} onClick={() => void query.refetch()}>
          {query.isFetching ? <Icons.spinner className='animate-spin' /> : <Icons.refresh />}
          重试
        </Button>
        {backLink}
      </div>
    </StateFrame>
  );
}

function StateFrame({ icon, title, description, children }: { icon: React.ReactNode; title: string; description: string; children: React.ReactNode }) {
  return (
    <div className='p-6'>
      <Empty className='border border-dashed'>
        <EmptyHeader>
          <EmptyMedia variant='icon'>{icon}</EmptyMedia>
          <EmptyTitle>{title}</EmptyTitle>
          <EmptyDescription>{description}</EmptyDescription>
        </EmptyHeader>
        <EmptyContent>{children}</EmptyContent>
      </Empty>
    </div>
  );
}
