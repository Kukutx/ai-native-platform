'use client';

import Link from 'next/link';
import { useQuery } from '@tanstack/react-query';
import { EmptyState } from '@/components/app/empty-state';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { api } from '@/lib/api';
import { memoryScope } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';

/** 混合检索结果：语义（向量）与关键词两路召回、融合排序，命中方式标在每条结果上。 */
export function SearchResults({ query }: { query: string }) {
  const results = useQuery({
    queryKey: queryKeys.knowledgeSearch(query),
    queryFn: ({ signal }) => api.knowledge.search(query, true, true, 20, undefined, signal),
  });

  if (results.isPending) {
    return <Skeleton className='h-40 w-full' />;
  }

  if (!results.data?.length) {
    return <EmptyState title='没有找到相关内容' description='换个说法试试，或者把相关资料添加到知识库。' />;
  }

  return (
    <ul className='space-y-2'>
      {results.data.map((hit) => (
        <li key={`${hit.sourceId}-${hit.seq}`} className='bg-card rounded-xl border p-3'>
          <div className='mb-1 flex flex-wrap items-center gap-2 text-sm'>
            {hit.sourceKind === 'document' ? (
              <>
                <Badge variant='secondary'>文档</Badge>
                <Link href={`/knowledge/view?id=${hit.sourceId}`} className='font-medium hover:underline'>
                  {hit.title}
                </Link>
              </>
            ) : (
              <>
                <Badge variant={hit.scope ? memoryScope[hit.scope].tone : 'outline'}>记忆</Badge>
                <span className='font-medium'>{hit.title}</span>
              </>
            )}
            <span className='text-muted-foreground ml-auto text-xs'>
              {[hit.semanticMatch ? '语义' : null, hit.keywordMatch ? '关键词' : null].filter(Boolean).join(' + ')}命中
            </span>
          </div>
          <p className='text-muted-foreground line-clamp-4 text-sm whitespace-pre-wrap'>{hit.content}</p>
        </li>
      ))}
    </ul>
  );
}
