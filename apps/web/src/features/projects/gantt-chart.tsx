'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import type { ProjectMilestoneDto, ProjectScheduleDto, ScheduledTaskDto, WorkItemStatus } from '@ainative/api-client';
import { Icons } from '@/components/icons';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Label } from '@/components/ui/label';
import { Switch } from '@/components/ui/switch';
import { api } from '@/lib/api';
import { formatDate, workItemStatus } from '@/lib/labels';
import { queryKeys } from '@/lib/query-keys';
import { cn } from '@/lib/utils';

type Zoom = 'day' | 'week' | 'month';
type Edit = 'move' | 'start' | 'end';
type Span = { start: number; end: number };
type Dates = { startDate?: string; dueDate?: string };
type Cell = { key: string; label: string; x: number; width: number; muted?: boolean; today?: boolean };
type Row = { kind: 'milestone'; milestone: ProjectMilestoneDto } | { kind: 'group'; label: string } | { kind: 'task'; task: ScheduledTaskDto };

const DAY_WIDTH: Record<Zoom, number> = { day: 36, week: 14, month: 5 };
const ZOOM_LABEL: Record<Zoom, string> = { day: '日', week: '周', month: '月' };
const ROW_HEIGHT = 36;
const BAR_HEIGHT = 20;
/** 左侧任务列宽度（手机上窄一些），月份标签吸附在它右边。 */
const LEFT_COLUMN = 'w-40 shrink-0 sm:w-[280px]';
const STICKY_LABEL = 'left-40 sm:left-[280px]';
const DAY_MS = 86_400_000;

const BAR_COLOR: Record<WorkItemStatus, string> = {
  backlog: 'bg-slate-400 dark:bg-slate-500',
  todo: 'bg-sky-500',
  in_progress: 'bg-blue-600',
  in_review: 'bg-violet-500',
  blocked: 'bg-amber-500',
  done: 'bg-emerald-500/70',
  cancelled: 'bg-muted-foreground/40',
};

/** 日期一律换算成自 1970-01-01 起的天数来计算，不受时区和夏令时影响。 */
function toDay(value: string) {
  const [year, month, day] = value.split('-').map(Number);
  return Date.UTC(year, month - 1, day) / DAY_MS;
}

function fromDay(day: number) {
  return new Date(day * DAY_MS).toISOString().slice(0, 10);
}

function todayDay() {
  const now = new Date();
  return Date.UTC(now.getFullYear(), now.getMonth(), now.getDate()) / DAY_MS;
}

/** 0 = 周一 … 6 = 周日（1970-01-01 是周四）。 */
function weekday(day: number) {
  return (((day + 3) % 7) + 7) % 7;
}

function monthOf(day: number, offset = 0) {
  const date = new Date(day * DAY_MS);
  return Date.UTC(date.getUTCFullYear(), date.getUTCMonth() + offset, 1) / DAY_MS;
}

function formatDay(day: number) {
  return formatDate(fromDay(day));
}

/** 时间条覆盖的日期：只有截止日期或只有开始日期的按那一天算；都没有的未排期。 */
function spanOf(dates: Dates): Span | null {
  const start = dates.startDate ?? dates.dueDate;
  const end = dates.dueDate ?? dates.startDate;
  return start && end ? { start: toDay(start), end: toDay(end) } : null;
}

/** 与后端一致：后续任务可以在前置任务截止当天开始，早于这一天才算冲突；关闭的任务不算。 */
function isConflict(pred: ScheduledTaskDto, predSpan: Span | null, succ: ScheduledTaskDto, succSpan: Span | null) {
  const closed = (t: ScheduledTaskDto) => t.status === 'done' || t.status === 'cancelled';
  return !!predSpan && !!succSpan && !closed(pred) && !closed(succ) && succSpan.start < predSpan.end;
}

/** 拖动或按键后的新日期。整体移动保持原来有哪些日期；拖两端会把单日任务变成一段时间。 */
function planEdit(task: ScheduledTaskDto, edit: Edit, delta: number): Dates | null {
  const span = spanOf(task);
  if (!span) {
    return null;
  }

  if (edit === 'move') {
    return {
      startDate: task.startDate ? fromDay(toDay(task.startDate) + delta) : undefined,
      dueDate: task.dueDate ? fromDay(toDay(task.dueDate) + delta) : undefined,
    };
  }

  return edit === 'start'
    ? { startDate: fromDay(Math.min(span.start + delta, span.end)), dueDate: fromDay(span.end) }
    : { startDate: fromDay(span.start), dueDate: fromDay(Math.max(span.end + delta, span.start)) };
}

function defaultZoom(data: ProjectScheduleDto): Zoom {
  const days = data.start && data.end ? toDay(data.end) - toDay(data.start) : 0;
  return days <= 45 ? 'day' : days <= 200 ? 'week' : 'month';
}

/** 显示范围：排期前后各留一点空，今天离得不远时一并包含；按周或按月对齐，至少铺满一屏。 */
function buildRange(data: ProjectScheduleDto, zoom: Zoom, today: number) {
  let start = data.start ? toDay(data.start) : today - 7;
  let end = data.end ? toDay(data.end) : today + 30;
  if (today >= start - 90 && today <= end + 90) {
    start = Math.min(start, today);
    end = Math.max(end, today);
  }

  const pad = zoom === 'day' ? 3 : zoom === 'week' ? 7 : 15;
  start -= pad;
  end = Math.max(end + pad, start + Math.ceil(1200 / DAY_WIDTH[zoom]));
  if (zoom === 'month') {
    start = monthOf(start);
    end = monthOf(end, 1) - 1;
  } else {
    start -= weekday(start);
    end += 6 - weekday(end);
  }

  return { start, days: end - start + 1 };
}

function headerCells(range: { start: number; days: number }, zoom: Zoom, today: number) {
  const end = range.start + range.days;
  const dw = DAY_WIDTH[zoom];
  const cell = (key: string, label: string, from: number, to: number, muted?: boolean): Cell => ({
    key,
    label,
    x: (Math.max(from, range.start) - range.start) * dw,
    width: (Math.min(to, end) - Math.max(from, range.start)) * dw,
    muted,
  });
  const top: Cell[] = [];
  const bottom: Cell[] = [];
  if (zoom === 'month') {
    for (let year = new Date(range.start * DAY_MS).getUTCFullYear(); Date.UTC(year, 0, 1) / DAY_MS < end; year++) {
      top.push(cell(`y${year}`, `${year} 年`, Date.UTC(year, 0, 1) / DAY_MS, Date.UTC(year + 1, 0, 1) / DAY_MS));
    }

    for (let month = range.start; month < end; month = monthOf(month, 1)) {
      bottom.push(cell(`m${month}`, `${new Date(month * DAY_MS).getUTCMonth() + 1} 月`, month, monthOf(month, 1)));
    }

    return { top, bottom };
  }

  for (let month = monthOf(range.start); month < end; month = monthOf(month, 1)) {
    const date = new Date(month * DAY_MS);
    top.push(cell(`m${month}`, `${date.getUTCFullYear()} 年 ${date.getUTCMonth() + 1} 月`, month, monthOf(month, 1)));
  }

  for (let day = range.start; day < end; day += zoom === 'day' ? 1 : 7) {
    const date = new Date(day * DAY_MS);
    bottom.push(
      zoom === 'day'
        ? { ...cell(`d${day}`, String(date.getUTCDate()), day, day + 1, weekday(day) >= 5), today: day === today }
        : cell(`w${day}`, `${date.getUTCMonth() + 1}/${date.getUTCDate()}`, day, day + 7),
    );
  }

  return { top, bottom };
}

/** 按里程碑分组，组内沿用后端的顺序（开始早的在前）。 */
function buildRows(data: ProjectScheduleDto): Row[] {
  const byMilestone = new Map<string, ScheduledTaskDto[]>();
  for (const task of data.tasks) {
    const key = task.milestoneId && data.milestones.some((m) => m.id === task.milestoneId) ? task.milestoneId : '';
    byMilestone.set(key, [...(byMilestone.get(key) ?? []), task]);
  }

  const rows: Row[] = [];
  for (const milestone of data.milestones) {
    rows.push({ kind: 'milestone', milestone });
    rows.push(...(byMilestone.get(milestone.id) ?? []).map((task) => ({ kind: 'task' as const, task })));
  }

  const rest = byMilestone.get('') ?? [];
  if (rest.length && data.milestones.length) {
    rows.push({ kind: 'group', label: '未归入里程碑' });
  }

  rows.push(...rest.map((task) => ({ kind: 'task' as const, task })));
  return rows;
}

/** 依赖箭头：从前置任务的末端折线连到后续任务的起点；空间不够（或冲突）时绕到行间再折回来。 */
function linkPath(x1: number, y1: number, x2: number, y2: number) {
  if (x2 - x1 >= 16) {
    return `M${x1},${y1} H${x1 + 8} V${y2} H${x2}`;
  }

  const turn = y2 > y1 ? y1 + ROW_HEIGHT / 2 : y1 - ROW_HEIGHT / 2;
  return `M${x1},${y1} H${x1 + 8} V${turn} H${x2 - 8} V${y2} H${x2}`;
}

/**
 * 项目甘特图：按里程碑分组的任务时间条、依赖箭头、排期冲突和关键路径。
 * 有任务管理权限时可以拖动时间条改日期（拖两端改开始或截止），默认把因此冲突的后续任务一起往后顺延。
 */
export function GanttChart({ data, editable }: { data: ProjectScheduleDto; editable: boolean }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [zoom, setZoom] = useState<Zoom>(() => defaultZoom(data));
  const [shift, setShift] = useState(true);
  const [preview, setPreview] = useState<{ id: string; span: Span } | null>(null);
  const [today] = useState(todayDay);
  const drag = useRef<{ task: ScheduledTaskDto; edit: Edit; originX: number; delta: number; moved: boolean } | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const scrolledFor = useRef<Zoom | null>(null);
  const dw = DAY_WIDTH[zoom];
  const range = useMemo(() => buildRange(data, zoom, today), [data, zoom, today]);
  const header = useMemo(() => headerCells(range, zoom, today), [range, zoom, today]);
  const rows = useMemo(() => buildRows(data), [data]);
  const tasks = useMemo(() => new Map(data.tasks.map((t) => [t.id, t])), [data]);
  const rowOf = useMemo(() => new Map(rows.flatMap((row, index) => (row.kind === 'task' ? [[row.task.id, index] as const] : []))), [rows]);
  const width = range.days * dw;
  const height = rows.length * ROW_HEIGHT;
  const x = (day: number) => (day - range.start) * dw;
  const spanFor = (task: ScheduledTaskDto) => (preview?.id === task.id ? preview.span : spanOf(task));
  const inRange = (day: number) => day >= range.start && day < range.start + range.days;

  // 切换刻度时把今天（今天不在范围内就是排期开始）滚到左侧附近。
  useEffect(() => {
    const element = scrollRef.current;
    if (!element || scrolledFor.current === zoom) {
      return;
    }

    scrolledFor.current = zoom;
    const focus = today >= range.start && today < range.start + range.days ? today : data.start ? toDay(data.start) : range.start;
    element.scrollLeft = Math.max(0, (focus - range.start) * DAY_WIDTH[zoom] - 120);
  }, [zoom, today, range, data.start]);

  const reschedule = useMutation({
    mutationFn: ({ task, dates }: { task: ScheduledTaskDto; dates: Dates }) =>
      api.workItems.schedule(task.id, { ...dates, shiftDependents: shift, expectedUpdatedAt: task.updatedAt }),
    onSuccess: (result) => {
      if (result.shifted.length) {
        toast.success(`已顺延 ${result.shifted.length} 个后续任务`, {
          description: result.shifted
            .slice(0, 5)
            .map((s) => `${s.key} 推迟 ${s.days} 天`)
            .join('，'),
        });
      }
    },
    onSettled: async (_result, _error, { task }) => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.project(data.project.id) }),
        queryClient.invalidateQueries({ queryKey: ['work-items'] }),
        queryClient.invalidateQueries({ queryKey: queryKeys.workItem(task.id) }),
      ]);
      setPreview(null);
    },
  });

  const commit = (task: ScheduledTaskDto, dates: Dates | null) => {
    const span = dates && spanOf(dates);
    if (!dates || !span || (dates.startDate === task.startDate && dates.dueDate === task.dueDate)) {
      setPreview(null);
      return;
    }

    setPreview({ id: task.id, span });
    reschedule.mutate({ task, dates });
  };

  if (!rows.length) {
    return <p className='text-muted-foreground py-8 text-center text-sm'>项目里还没有任务和里程碑。</p>;
  }

  return (
    <div className='space-y-3'>
      <div className='flex flex-wrap items-center justify-between gap-3'>
        <div className='flex flex-wrap items-center gap-4'>
          <div className='inline-flex rounded-md border p-0.5' role='group' aria-label='时间刻度'>
            {(Object.keys(ZOOM_LABEL) as Zoom[]).map((z) => (
              <Button key={z} size='sm' variant={zoom === z ? 'secondary' : 'ghost'} className='h-7 px-3' aria-pressed={zoom === z} onClick={() => setZoom(z)}>
                {ZOOM_LABEL[z]}
              </Button>
            ))}
          </div>
          {editable ? (
            <div className='flex items-center gap-2'>
              <Switch id='gantt-shift' checked={shift} onCheckedChange={setShift} />
              <Label htmlFor='gantt-shift' className='font-normal'>
                顺延后续任务
              </Label>
            </div>
          ) : null}
        </div>
        <div className='text-muted-foreground flex flex-wrap items-center gap-3 text-xs'>
          {data.conflicts ? <Badge variant='destructive'>{data.conflicts} 处排期冲突</Badge> : null}
          {data.unscheduled ? <span>{data.unscheduled} 个任务未排期</span> : null}
          <span className='flex items-center gap-1.5'>
            <span className='ring-offset-background inline-block h-2.5 w-5 rounded-sm bg-sky-500 ring-2 ring-red-500 ring-offset-1' />
            关键路径
          </span>
          <span className='flex items-center gap-1.5'>
            <svg width='20' height='6' aria-hidden>
              <line x1='0' y1='3' x2='20' y2='3' className='stroke-red-500' strokeWidth='1.5' strokeDasharray='4 3' />
            </svg>
            冲突的依赖
          </span>
          <span className='flex items-center gap-1.5'>
            <span className='inline-block h-3 w-px bg-red-500' />
            今天
          </span>
        </div>
      </div>
      <div ref={scrollRef} className='bg-background max-h-[70vh] overflow-auto rounded-md border'>
        <div className='relative w-max'>
          <div className='bg-background sticky top-0 z-20 flex border-b'>
            <div
              className={cn('bg-background text-muted-foreground sticky left-0 z-30 flex items-end border-r px-3 pb-1.5 text-xs', LEFT_COLUMN)}
              style={{ height: 48 }}
            >
              任务
            </div>
            <div className='relative shrink-0' style={{ width, height: 48 }}>
              {header.top.map((c) => (
                <div
                  key={c.key}
                  className='absolute top-0 h-6 overflow-clip border-l text-xs leading-6 font-medium whitespace-nowrap'
                  style={{ left: c.x, width: c.width }}
                >
                  {/* 月份（或年份）标签贴在可见区域左侧，横向滚动时不会被卷走。 */}
                  <span className={cn('sticky px-1.5', STICKY_LABEL)}>{c.label}</span>
                </div>
              ))}
              {header.bottom.map((c) => (
                <div
                  key={c.key}
                  className={cn(
                    'absolute top-6 h-6 overflow-hidden border-t border-l text-[11px] leading-6 whitespace-nowrap tabular-nums',
                    zoom === 'day' ? 'text-center' : 'px-1',
                    c.today ? 'font-semibold text-red-600 dark:text-red-500' : c.muted ? 'text-muted-foreground/60' : 'text-muted-foreground',
                  )}
                  style={{ left: c.x, width: c.width }}
                >
                  {zoom === 'month' && c.width < 24 ? '' : c.label}
                </div>
              ))}
              {inRange(today) ? (
                <div className='absolute bottom-0 size-1.5 -translate-x-1/2 rounded-full bg-red-500' style={{ left: x(today) + dw / 2 }} title='今天' />
              ) : null}
            </div>
          </div>
          <div className='flex'>
            <div className={cn('bg-background sticky left-0 z-10 border-r', LEFT_COLUMN)}>
              {rows.map((row) => (
                <RowLabel key={row.kind === 'task' ? row.task.id : row.kind === 'milestone' ? row.milestone.id : 'group'} row={row} />
              ))}
            </div>
            <div
              className='relative shrink-0'
              style={{
                width,
                height,
                backgroundImage:
                  zoom === 'month'
                    ? undefined
                    : `repeating-linear-gradient(to right, transparent 0 ${5 * dw}px, color-mix(in oklab, var(--muted) 80%, transparent) ${5 * dw}px ${7 * dw}px)`,
              }}
            >
              {header.bottom.map((c) => (
                <div key={c.key} className='border-border/60 pointer-events-none absolute inset-y-0 border-l' style={{ left: c.x }} />
              ))}
              {rows.map((row, index) => {
                const top = index * ROW_HEIGHT;
                if (row.kind !== 'task') {
                  return <div key={`bg${index}`} className='bg-muted/40 pointer-events-none absolute inset-x-0 border-b' style={{ top, height: ROW_HEIGHT }} />;
                }

                const undated = !spanFor(row.task);
                const schedulable = undated && editable && row.task.status !== 'done' && !reschedule.isPending;
                return (
                  <div
                    key={`bg${index}`}
                    className={cn('absolute inset-x-0 border-b', schedulable && 'hover:bg-accent/40 cursor-copy')}
                    style={{ top, height: ROW_HEIGHT }}
                    title={schedulable ? '双击把这个任务安排到这一天' : undefined}
                    onDoubleClick={(e) => {
                      if (!schedulable) {
                        return;
                      }

                      const day = range.start + Math.floor((e.clientX - e.currentTarget.getBoundingClientRect().left) / dw);
                      commit(row.task, { startDate: fromDay(day), dueDate: fromDay(day) });
                    }}
                  />
                );
              })}
              {data.project.targetDate && inRange(toDay(data.project.targetDate)) ? (
                <div
                  className='border-primary/50 pointer-events-none absolute inset-y-0 border-l border-dashed'
                  style={{ left: x(toDay(data.project.targetDate) + 1) }}
                  title={`项目目标 ${formatDate(data.project.targetDate)}`}
                />
              ) : null}
              {inRange(today) ? <div className='pointer-events-none absolute inset-y-0 z-[1] w-px bg-red-500/70' style={{ left: x(today) + dw / 2 }} /> : null}
              <svg className='pointer-events-none absolute inset-0 z-[2]' width={width} height={height} aria-hidden>
                <defs>
                  <marker id='gantt-arrow' viewBox='0 0 8 8' refX='7' refY='4' markerWidth='6' markerHeight='6' orient='auto' className='text-muted-foreground'>
                    <path d='M0,0 L8,4 L0,8 z' fill='currentColor' />
                  </marker>
                  <marker id='gantt-arrow-conflict' viewBox='0 0 8 8' refX='7' refY='4' markerWidth='6' markerHeight='6' orient='auto' className='text-red-500'>
                    <path d='M0,0 L8,4 L0,8 z' fill='currentColor' />
                  </marker>
                </defs>
                {data.links.map((link) => {
                  const pred = tasks.get(link.fromId);
                  const succ = tasks.get(link.toId);
                  const predRow = rowOf.get(link.fromId);
                  const succRow = rowOf.get(link.toId);
                  const predSpan = pred && spanFor(pred);
                  const succSpan = succ && spanFor(succ);
                  if (!pred || !succ || predRow === undefined || succRow === undefined || !predSpan || !succSpan) {
                    return null;
                  }

                  const conflict = isConflict(pred, predSpan, succ, succSpan);
                  return (
                    <path
                      key={`${link.fromId}-${link.toId}`}
                      d={linkPath(x(predSpan.end + 1), predRow * ROW_HEIGHT + ROW_HEIGHT / 2, x(succSpan.start) - 1, succRow * ROW_HEIGHT + ROW_HEIGHT / 2)}
                      fill='none'
                      strokeWidth={conflict ? 1.5 : 1.25}
                      strokeDasharray={conflict ? '4 3' : undefined}
                      className={conflict ? 'stroke-red-500' : 'stroke-muted-foreground/70'}
                      markerEnd={conflict ? 'url(#gantt-arrow-conflict)' : 'url(#gantt-arrow)'}
                    />
                  );
                })}
              </svg>
              {rows.map((row, index) => {
                if (row.kind === 'milestone') {
                  const m = row.milestone;
                  if (!m.dueDate || !inRange(toDay(m.dueDate))) {
                    return null;
                  }

                  return (
                    <div
                      key={m.id}
                      className={cn(
                        'absolute z-[3] size-3 rotate-45 rounded-[2px]',
                        m.completedAt ? 'bg-emerald-500' : m.isOverdue ? 'bg-red-500' : 'bg-primary',
                      )}
                      style={{ left: x(toDay(m.dueDate)) + dw / 2 - 6, top: index * ROW_HEIGHT + ROW_HEIGHT / 2 - 6 }}
                      title={`${m.name} · ${formatDate(m.dueDate)} 截止${m.completedAt ? '（已完成）' : m.isOverdue ? '（已逾期）' : ''}`}
                    />
                  );
                }

                if (row.kind !== 'task') {
                  return null;
                }

                const task = row.task;
                const span = spanFor(task);
                if (!span) {
                  return null;
                }

                const movable = editable && task.status !== 'done' && !reschedule.isPending;
                const left = x(span.start);
                const barWidth = Math.max((span.end - span.start + 1) * dw, 6);
                // 时间条够宽时标题写在条里，否则写在右边。
                const inside = barWidth >= 56;
                const top = index * ROW_HEIGHT + (ROW_HEIGHT - BAR_HEIGHT) / 2;
                const tooltip = [
                  `${task.key} ${task.title}`,
                  `${formatDay(span.start)} – ${formatDay(span.end)}（${span.end - span.start + 1} 天）`,
                  workItemStatus[task.status].label + (task.assignee?.name ? ` · ${task.assignee.name}` : ''),
                  task.critical ? '关键路径：它晚一天，项目就跟着晚一天' : null,
                  task.waitingOn ? `等待 ${task.waitingOn} 个前置任务关闭` : null,
                  task.isOverdue ? '已逾期' : null,
                ]
                  .filter(Boolean)
                  .join('\n');
                return (
                  <div key={task.id}>
                    <div
                      role='button'
                      tabIndex={0}
                      aria-label={`${task.key} ${task.title}，${formatDay(span.start)} 至 ${formatDay(span.end)}${movable ? '，拖动或用方向键调整日期' : ''}`}
                      title={tooltip}
                      className={cn(
                        'focus-visible:outline-ring absolute z-[3] touch-none rounded-sm shadow-sm select-none focus-visible:outline-2 focus-visible:outline-offset-2',
                        BAR_COLOR[task.status],
                        task.critical && 'ring-offset-background ring-2 ring-red-500 ring-offset-1',
                        preview?.id === task.id && 'opacity-80',
                        movable ? 'cursor-grab active:cursor-grabbing' : 'cursor-pointer',
                      )}
                      style={{ left, width: barWidth, top, height: BAR_HEIGHT }}
                      onPointerDown={(e) => {
                        if (e.button !== 0) {
                          return;
                        }

                        e.currentTarget.setPointerCapture(e.pointerId);
                        drag.current = {
                          task,
                          edit: ((e.target as HTMLElement).dataset.edit as Edit | undefined) ?? 'move',
                          originX: e.clientX,
                          delta: 0,
                          moved: false,
                        };
                      }}
                      onPointerMove={(e) => {
                        const current = drag.current;
                        if (!current || current.task.id !== task.id) {
                          return;
                        }

                        const dx = e.clientX - current.originX;
                        current.moved ||= Math.abs(dx) > 3;
                        const delta = movable ? Math.round(dx / dw) : 0;
                        if (delta !== current.delta) {
                          current.delta = delta;
                          const next = spanOf(planEdit(task, current.edit, delta) ?? {});
                          setPreview(next ? { id: task.id, span: next } : null);
                        }
                      }}
                      onPointerUp={() => {
                        const current = drag.current;
                        drag.current = null;
                        if (!current) {
                          return;
                        }

                        if (!current.moved) {
                          router.push(`/tasks/view?id=${task.id}`);
                        } else if (movable) {
                          commit(task, planEdit(task, current.edit, current.delta));
                        }
                      }}
                      onPointerCancel={() => {
                        drag.current = null;
                        setPreview(null);
                      }}
                      onKeyDown={(e) => {
                        if (e.key === 'Enter') {
                          router.push(`/tasks/view?id=${task.id}`);
                          return;
                        }

                        if (!movable || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) {
                          return;
                        }

                        e.preventDefault();
                        commit(task, planEdit(task, e.shiftKey ? 'end' : 'move', e.key === 'ArrowLeft' ? -1 : 1));
                      }}
                    >
                      {inside ? <span className='pointer-events-none block truncate px-2 text-xs leading-5 font-medium text-white'>{task.title}</span> : null}
                      {movable ? (
                        <>
                          <span data-edit='start' className='absolute inset-y-0 left-0 w-1.5 cursor-ew-resize' />
                          <span data-edit='end' className='absolute inset-y-0 right-0 w-1.5 cursor-ew-resize' />
                        </>
                      ) : null}
                    </div>
                    {inside ? null : (
                      <span
                        className='text-muted-foreground pointer-events-none absolute z-[3] text-xs leading-5 whitespace-nowrap'
                        style={{ left: left + barWidth + 6, top }}
                      >
                        {task.title}
                      </span>
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>
      <p className='text-muted-foreground text-xs'>
        {editable
          ? '拖动时间条调整日期，拖两端改开始或截止日期；选中时间条后可用 ← → 移动一天，Shift + ← → 改截止日期，回车打开任务。未排期的任务在它那一行双击即可安排。'
          : '点击时间条打开任务。'}
        {data.truncated ? ' 任务太多，只排了其中 500 个（优先未完成、截止早的）。' : null}
      </p>
    </div>
  );
}

function RowLabel({ row }: { row: Row }) {
  if (row.kind === 'milestone') {
    const m = row.milestone;
    return (
      <div className='bg-muted/40 flex items-center gap-2 border-b px-3 text-sm font-medium' style={{ height: ROW_HEIGHT }}>
        <Icons.milestone className='text-muted-foreground size-4 shrink-0' />
        <span className='min-w-0 flex-1 truncate'>{m.name}</span>
        <span className='text-muted-foreground shrink-0 text-xs font-normal tabular-nums'>{m.dueDate ? formatDate(m.dueDate) : '无截止'}</span>
      </div>
    );
  }

  if (row.kind === 'group') {
    return (
      <div className='bg-muted/40 text-muted-foreground flex items-center border-b px-3 text-sm font-medium' style={{ height: ROW_HEIGHT }}>
        {row.label}
      </div>
    );
  }

  const task = row.task;
  const closed = task.status === 'done' || task.status === 'cancelled';
  return (
    <div className='flex items-center gap-2 border-b px-3 text-sm' style={{ height: ROW_HEIGHT }}>
      <span className='text-muted-foreground shrink-0 font-mono text-xs'>{task.key}</span>
      <Link
        href={`/tasks/view?id=${task.id}`}
        className={cn('min-w-0 flex-1 truncate hover:underline', closed && 'text-muted-foreground line-through')}
        title={task.title}
      >
        {task.title}
      </Link>
      {task.waitingOn && !closed ? (
        <span title={`等待 ${task.waitingOn} 个前置任务关闭`} className='shrink-0'>
          <Icons.hourglass className='size-3.5 text-amber-600 dark:text-amber-500' aria-label={`等待 ${task.waitingOn} 个前置任务关闭`} />
        </span>
      ) : null}
      {!task.startDate && !task.dueDate ? <span className='text-muted-foreground shrink-0 text-xs'>未排期</span> : null}
      {task.assignee?.name ? <span className='text-muted-foreground hidden max-w-20 shrink-0 truncate text-xs sm:inline'>{task.assignee.name}</span> : null}
    </div>
  );
}
