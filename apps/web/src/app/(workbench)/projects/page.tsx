'use client';

import { Suspense, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ProjectStatus } from '@ainative/api-client';
import { EmptyState } from '@/components/app/empty-state';
import { PageHeader } from '@/components/app/page-header';
import { Pager } from '@/components/app/pager';
import { ErrorState } from '@/components/app/query-state';
import { useSession } from '@/components/app/session-provider';
import { StatusBadge } from '@/components/app/status-badge';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ProgressBar } from '@/features/projects/progress-bar';
import { ProjectFormDialog } from '@/features/projects/project-form-dialog';
import { pickOption, useSearchText, useUrlState } from '@/hooks/use-url-state';
import { api } from '@/lib/api';
import { formatDate, projectStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

const PAGE_SIZE = 50;
const OPEN = 'open';
const ALL = 'all';

export default function ProjectsPage() {
  return (
    <Suspense>
      <Projects />
    </Suspense>
  );
}

function Projects() {
  const { can } = useSession();
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [filters, setFilters] = useUrlState({ q: '', status: OPEN, page: 1 });
  const { q: term, page } = filters;
  const status: ProjectStatus | undefined = pickOption(filters.status, projectStatus);
  const filter = status ?? (filters.status === ALL ? ALL : OPEN);
  const [search, setSearch] = useSearchText(term, (q) => setFilters({ q, page: 1 }));
  const projects = useQuery({
    queryKey: queryKeys.projects({ search: term, filter, page }),
    queryFn: ({ signal }) => api.projects.list(term || undefined, status, filter === OPEN, page, PAGE_SIZE, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <div className='space-y-6 p-6'>
      <PageHeader
        title='项目'
        description='把任务按项目和里程碑组织起来，进度、受阻和逾期实时统计。负责人可以是智能体。'
        actions={
          <>
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder='搜索项目'
              aria-label='搜索项目'
              className='w-48'
            />
            <Select
              value={filter}
              onValueChange={(v) => setFilters({ status: v, page: 1 })}
            >
              <SelectTrigger className='w-32'>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={OPEN}>未结束</SelectItem>
                <SelectItem value={ALL}>全部</SelectItem>
                {Object.entries(projectStatus).map(([key, meta]) => (
                  <SelectItem key={key} value={key}>
                    {meta.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {can('projects.manage') ? (
              <Button onClick={() => setCreating(true)}>
                <Icons.add />
                新建项目
              </Button>
            ) : null}
          </>
        }
      />
      {projects.isPending ? (
        <Skeleton className='h-48 w-full' />
      ) : projects.isError && !projects.data ? (
        <ErrorState query={projects} />
      ) : projects.data?.items.length ? (
        <Card>
          <CardContent>
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>项目</TableHead>
                  <TableHead>状态</TableHead>
                  <TableHead className='w-64'>进度</TableHead>
                  <TableHead>负责人</TableHead>
                  <TableHead>目标日期</TableHead>
                  <TableHead className='text-right'>里程碑</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {projects.data.items.map((p) => (
                  <TableRow key={p.id}>
                    <TableCell className='max-w-xs'>
                      <Link href={`/projects/view?id=${p.id}`} className='font-medium hover:underline'>
                        {p.name}
                      </Link>
                      {p.description ? <p className='text-muted-foreground line-clamp-1 text-xs'>{p.description}</p> : null}
                    </TableCell>
                    <TableCell>
                      <StatusBadge meta={projectStatus[p.status]} />
                    </TableCell>
                    <TableCell>
                      <ProgressBar progress={p.progress} compact />
                    </TableCell>
                    <TableCell className='text-muted-foreground'>
                      {p.lead?.name ?? '—'}
                      {p.lead?.kind === 'agent' ? <span className='ml-1 text-xs'>（智能体）</span> : null}
                    </TableCell>
                    <TableCell className='text-muted-foreground'>
                      {formatDate(p.targetDate)}
                      {p.isOverdue ? (
                        <Badge variant='destructive' className='ml-2'>
                          已逾期
                        </Badge>
                      ) : null}
                    </TableCell>
                    <TableCell className='text-right tabular-nums'>{p.milestoneCount}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <Pager page={page} pageSize={PAGE_SIZE} total={projects.data.total} onChange={(next) => setFilters({ page: next })} />
          </CardContent>
        </Card>
      ) : (
        <EmptyState
          title={term || filter !== OPEN ? '没有符合条件的项目' : '还没有项目'}
          description={term || filter !== OPEN ? '换个条件试试。' : '新建项目并设好里程碑，或者在对话里让智能体帮你规划一个。'}
        />
      )}
      <ProjectFormDialog open={creating} onOpenChange={setCreating} onSaved={(id) => router.push(`/projects/view?id=${id}`)} />
    </div>
  );
}
