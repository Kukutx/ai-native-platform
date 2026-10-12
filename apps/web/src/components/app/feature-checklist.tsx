'use client';

import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { FeatureDto, ModuleDto } from '@ainative/api-client';
import { InlineError } from '@/components/app/query-state';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

const ALL = '*';

/** 与后端 FeatureMatcher 一致：精确匹配、前缀通配（"tasks.*"）或全部（"*"）。 */
export function featureGranted(grants: readonly string[], feature: string) {
  return grants.some((grant) => grant === ALL || grant === feature || (grant.endsWith('.*') && feature.startsWith(grant.slice(0, -1))));
}

/** 只由通配授予（不是逐项勾选的）。 */
function grantedByWildcard(grants: readonly string[], feature: string) {
  return featureGranted(
    grants.filter((g) => g === ALL || g.endsWith('.*')),
    feature,
  );
}

/** 通配权限覆盖的功能。 */
function coveredBy(grant: string, features: readonly FeatureDto[]) {
  return features.filter((f) => featureGranted([grant], f.key));
}

/** 模块里各功能键的前缀（例如智能体模块包含 agents、tasks、runs、approvals）。 */
function prefixesOf(module: ModuleDto) {
  return [...new Set(module.features.map((f) => f.key.split('.')[0]))];
}

/**
 * 功能权限目录：来自模块列表（每个模块声明的权限，所有成员都能读取），
 * 同时提供把权限键（含通配）转成中文名称的方法。
 */
export function useFeatureCatalog() {
  const modules = useQuery({ queryKey: queryKeys.modules, queryFn: ({ signal }) => api.modules.list(signal) });
  const label = useMemo(() => {
    const list = modules.data ?? [];
    const features = list.flatMap((m) => m.features);
    const names = new Map(features.map((f) => [f.key, f.name]));
    return (key: string) => {
      if (key === ALL) {
        return '全部权限';
      }

      if (!key.endsWith('.*')) {
        return names.get(key) ?? key;
      }

      const covered = coveredBy(key, features);
      const owner = list.find((m) => m.key === key.slice(0, -2));
      if (owner && covered.length === owner.features.length) {
        return `${owner.name}（全部）`;
      }

      if (!covered.length) {
        return key;
      }

      return covered.length <= 3
        ? covered.map((f) => f.name).join('、')
        : `${covered
            .slice(0, 2)
            .map((f) => f.name)
            .join('、')}等 ${covered.length} 项`;
    };
  }, [modules.data]);

  return { modules, label };
}

/**
 * 按模块分组勾选功能权限，角色和智能体共用。支持模块整体授予（写成通配，之后新增的权限自动包含）。
 * 目录里没有的权限（例如已下线的功能）原样保留，可以单独移除。
 */
export function FeatureChecklist({ value, onChange, disabled }: { value: string[]; onChange: (value: string[]) => void; disabled?: boolean }) {
  const { modules, label } = useFeatureCatalog();

  if (modules.isPending) {
    return (
      <div className='grid gap-2'>
        <Skeleton className='h-24' />
        <Skeleton className='h-24' />
      </div>
    );
  }

  if (!modules.data) {
    return <InlineError query={modules} />;
  }

  const features = modules.data.flatMap((m) => m.features);
  const unknown = value.filter((g) => g !== ALL && !coveredBy(g, features).length);
  const add = (keys: string[]) => onChange([...new Set([...value, ...keys])]);
  const remove = (keys: string[]) => onChange(value.filter((v) => !keys.includes(v)));

  return (
    <div className='grid gap-3'>
      {value.includes(ALL) ? (
        <label className='bg-muted/50 flex items-center gap-2 rounded-md border p-3 text-sm font-medium'>
          <input type='checkbox' className='accent-primary size-4' checked disabled={disabled} onChange={() => remove([ALL])} />
          全部权限
          <span className='text-muted-foreground text-xs font-normal'>包含所有模块现在和以后新增的权限</span>
        </label>
      ) : null}
      {modules.data
        .filter((m) => m.features.length)
        .map((group) => {
          const wildcards = prefixesOf(group).map((p) => `${p}.*`);
          const all = group.features.every((f) => grantedByWildcard(value, f.key));
          return (
            <div key={group.key} className='rounded-md border p-3'>
              <label className='mb-2 flex flex-wrap items-center gap-2 text-sm font-medium'>
                <input
                  type='checkbox'
                  className='accent-primary size-4'
                  checked={all}
                  disabled={disabled || value.includes(ALL)}
                  onChange={(e) =>
                    e.target.checked
                      ? // 整体授予后逐项勾选就多余了，一并清掉，保持列表简洁。
                        onChange([...new Set([...value.filter((v) => !group.features.some((f) => f.key === v)), ...wildcards])])
                      : remove(wildcards)
                  }
                />
                {group.name}（全部）
                {group.enabled ? null : <Badge variant='outline'>未启用</Badge>}
              </label>
              <div className='grid gap-1 sm:grid-cols-2'>
                {group.features.map((f) => {
                  const inherited = grantedByWildcard(value, f.key);
                  return (
                    <label key={f.key} className='flex items-start gap-2 text-sm'>
                      <input
                        type='checkbox'
                        className='accent-primary mt-0.5 size-4 shrink-0'
                        disabled={disabled || inherited}
                        checked={featureGranted(value, f.key)}
                        onChange={(e) => (e.target.checked ? add([f.key]) : remove([f.key]))}
                      />
                      <span className='min-w-0'>
                        {f.name}
                        <span className='text-muted-foreground block font-mono text-[11px] break-all'>{f.key}</span>
                      </span>
                    </label>
                  );
                })}
              </div>
            </div>
          );
        })}
      {unknown.length ? (
        <div className='space-y-2 rounded-md border border-dashed p-3 text-sm'>
          <p className='text-muted-foreground text-xs'>以下权限不在当前的功能目录里，会原样保留：</p>
          <div className='flex flex-wrap gap-1'>
            {unknown.map((g) => (
              <Badge key={g} variant='secondary' className='gap-1 pr-1 font-mono text-[11px]'>
                {label(g)}
                <Button
                  type='button'
                  size='icon'
                  variant='ghost'
                  className='size-4'
                  disabled={disabled}
                  onClick={() => remove([g])}
                  aria-label={`移除权限 ${g}`}
                >
                  <Icons.close className='size-3' />
                </Button>
              </Badge>
            ))}
          </div>
        </div>
      ) : null}
    </div>
  );
}
