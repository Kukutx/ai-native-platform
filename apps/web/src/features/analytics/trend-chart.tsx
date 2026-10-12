'use client';

import { useId, useMemo, useState } from 'react';
import type { AnalyticsInterval, MetricPointDto, MetricUnit } from '@ainative/api-client';
import { useElementWidth } from '@/hooks/use-element-width';
import { cn } from '@/lib/utils';
import { describeBucket, formatBucket, formatMetric } from './format';

export interface ChartSeries {
  key: string;
  label: string;
  points: MetricPointDto[];
}

/** 后端把排名靠后的分组合并成这个键。 */
export const OTHER_SERIES = '__other__';

const PALETTE = [
  'var(--chart-2)',
  'var(--chart-1)',
  'oklch(0.65 0.15 160)',
  'oklch(0.63 0.2 20)',
  'oklch(0.6 0.18 300)',
  'oklch(0.7 0.13 220)',
  'oklch(0.7 0.15 125)',
  'oklch(0.62 0.13 50)',
];

export function seriesColor(index: number, key?: string) {
  return key === OTHER_SERIES ? 'var(--muted-foreground)' : PALETTE[index % PALETTE.length];
}

const PADDING = { top: 12, right: 12, bottom: 24, left: 56 };

/** 坐标轴刻度：0 起，取 1/2/5×10ⁿ 的整齐步长。 */
function niceTicks(max: number, count = 4) {
  const raw = max / count;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * magnitude).find((s) => s >= raw) ?? raw;
  const ticks: number[] = [];
  for (let v = 0; v <= max + step * 1e-9; v += step) {
    ticks.push(v);
  }

  if (ticks[ticks.length - 1] < max) {
    ticks.push(ticks[ticks.length - 1] + step);
  }

  return ticks;
}

/**
 * 趋势图（纯 SVG）：折线或柱状，多组时柱状图堆叠显示。鼠标悬停显示该时间段各组的数值。
 * 比率类指标（stacked=false）不堆叠，柱状图只画第一组。
 */
export function TrendChart({
  series,
  interval,
  unit,
  currency,
  kind,
  stacked = true,
  height = 220,
  className,
}: {
  series: ChartSeries[];
  interval: AnalyticsInterval;
  unit: MetricUnit;
  currency?: string | null;
  kind: 'line' | 'bar';
  stacked?: boolean;
  height?: number;
  className?: string;
}) {
  const [ref, width] = useElementWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  // useId 含冒号等字符，作 SVG 引用前去掉。
  const gradientId = `trend${useId().replace(/[^a-zA-Z0-9_-]/g, '')}`;
  const drawn = kind === 'bar' && !stacked ? series.slice(0, 1) : series;
  const dates = useMemo(() => drawn[0]?.points.map((p) => p.date) ?? [], [drawn]);
  const count = dates.length;
  const withYear = count > 0 && dates[0].slice(0, 4) !== dates[count - 1].slice(0, 4);

  const geometry = useMemo(() => {
    const innerWidth = Math.max(width - PADDING.left - PADDING.right, 1);
    const innerHeight = height - PADDING.top - PADDING.bottom;
    const stackedTotals = dates.map((_, i) => drawn.reduce((sum, s) => sum + Math.max(s.points[i]?.value ?? 0, 0), 0));
    const max =
      kind === 'bar' && stacked
        ? Math.max(...stackedTotals, 0)
        : Math.max(...drawn.flatMap((s) => s.points.map((p) => p.value ?? 0)), 0);
    const ticks = niceTicks(max > 0 ? max : 1);
    const top = ticks[ticks.length - 1];
    const band = innerWidth / Math.max(count, 1);
    const x = (i: number) =>
      kind === 'bar' ? PADDING.left + band * (i + 0.5) : PADDING.left + (count <= 1 ? innerWidth / 2 : (i * innerWidth) / (count - 1));
    const y = (v: number) => PADDING.top + innerHeight - (v / top) * innerHeight;
    return { innerWidth, innerHeight, ticks, band, x, y, stackedTotals };
  }, [width, height, dates, drawn, kind, stacked, count]);

  const labelStep = Math.max(1, Math.ceil(count / Math.max(1, Math.floor(geometry.innerWidth / 64))));

  const indexAt = (clientX: number, rect: DOMRect) => {
    const offset = clientX - rect.left - PADDING.left;
    const raw = kind === 'bar' ? Math.floor(offset / geometry.band) : Math.round(count <= 1 ? 0 : (offset * (count - 1)) / geometry.innerWidth);
    return Math.min(Math.max(raw, 0), count - 1);
  };

  return (
    <div className={cn('relative w-full', className)} ref={ref} style={{ height }}>
      {width > 0 && count > 0 ? (
        <svg
          width={width}
          height={height}
          role='img'
          aria-label={drawn.map((s) => s.label).join('、')}
          className='overflow-visible'
          onPointerMove={(event) => setHover(indexAt(event.clientX, event.currentTarget.getBoundingClientRect()))}
          onPointerLeave={() => setHover(null)}
        >
          {geometry.ticks.map((tick) => (
            <g key={tick}>
              <line
                x1={PADDING.left}
                x2={width - PADDING.right}
                y1={geometry.y(tick)}
                y2={geometry.y(tick)}
                className='stroke-border'
                strokeDasharray={tick === 0 ? undefined : '3 3'}
              />
              <text x={PADDING.left - 8} y={geometry.y(tick)} dy='0.32em' textAnchor='end' className='fill-muted-foreground text-[11px] tabular-nums'>
                {formatMetric(tick, unit, currency, true)}
              </text>
            </g>
          ))}
          {dates.map((date, i) =>
            i % labelStep === 0 ? (
              <text key={date} x={geometry.x(i)} y={height - 6} textAnchor='middle' className='fill-muted-foreground text-[11px] tabular-nums'>
                {formatBucket(date, interval, withYear)}
              </text>
            ) : null,
          )}

          {kind === 'bar' ? (
            <Bars series={drawn} geometry={geometry} stacked={stacked} hover={hover} />
          ) : (
            <Lines series={drawn} geometry={geometry} gradientId={gradientId} />
          )}

          {hover !== null ? (
            <g pointerEvents='none'>
              {kind === 'line' ? (
                <>
                  <line
                    x1={geometry.x(hover)}
                    x2={geometry.x(hover)}
                    y1={PADDING.top}
                    y2={PADDING.top + geometry.innerHeight}
                    className='stroke-muted-foreground/50'
                  />
                  {drawn.map((s, index) =>
                    s.points[hover]?.value !== undefined && s.points[hover]?.value !== null ? (
                      <circle
                        key={s.key}
                        cx={geometry.x(hover)}
                        cy={geometry.y(s.points[hover].value ?? 0)}
                        r={3.5}
                        fill={seriesColor(index, s.key)}
                        className='stroke-background'
                        strokeWidth={1.5}
                      />
                    ) : null,
                  )}
                </>
              ) : null}
            </g>
          ) : null}
        </svg>
      ) : null}
      {hover !== null && width > 0 ? (
        <ChartTooltip
          left={geometry.x(hover)}
          width={width}
          title={describeBucket(dates[hover], interval)}
          rows={drawn.map((s, index) => ({ key: s.key, label: s.label, color: seriesColor(index, s.key), value: s.points[hover]?.value }))}
          total={kind === 'bar' && stacked && drawn.length > 1 ? geometry.stackedTotals[hover] : undefined}
          unit={unit}
          currency={currency}
        />
      ) : null}
    </div>
  );
}

type Geometry = {
  innerHeight: number;
  band: number;
  x: (i: number) => number;
  y: (v: number) => number;
};

function Lines({ series, geometry, gradientId }: { series: ChartSeries[]; geometry: Geometry; gradientId: string }) {
  return (
    <>
      {series.map((s, index) => {
        const color = seriesColor(index, s.key);
        // 没有值的时间段（比率类指标当期没有样本）断开折线。
        const segments: { x: number; y: number }[][] = [];
        let current: { x: number; y: number }[] = [];
        s.points.forEach((p, i) => {
          if (p.value === null || p.value === undefined) {
            if (current.length) {
              segments.push(current);
            }

            current = [];
            return;
          }

          current.push({ x: geometry.x(i), y: geometry.y(p.value) });
        });
        if (current.length) {
          segments.push(current);
        }

        const baseline = geometry.y(0);
        return (
          <g key={s.key}>
            {series.length === 1 ? (
              <>
                <defs>
                  <linearGradient id={gradientId} x1='0' x2='0' y1='0' y2='1'>
                    <stop offset='0%' stopColor={color} stopOpacity={0.25} />
                    <stop offset='100%' stopColor={color} stopOpacity={0} />
                  </linearGradient>
                </defs>
                {segments.map((segment, i) => (
                  <path
                    key={`area-${i}`}
                    d={`M${segment[0].x},${baseline} ${segment.map((p) => `L${p.x},${p.y}`).join(' ')} L${segment[segment.length - 1].x},${baseline} Z`}
                    fill={`url(#${gradientId})`}
                  />
                ))}
              </>
            ) : null}
            {segments.map((segment, i) =>
              segment.length === 1 ? (
                <circle key={i} cx={segment[0].x} cy={segment[0].y} r={2.5} fill={color} />
              ) : (
                <path
                  key={i}
                  d={segment.map((p, j) => `${j === 0 ? 'M' : 'L'}${p.x},${p.y}`).join(' ')}
                  fill='none'
                  stroke={color}
                  strokeWidth={2}
                  strokeLinejoin='round'
                  strokeLinecap='round'
                />
              ),
            )}
          </g>
        );
      })}
    </>
  );
}

function Bars({ series, geometry, stacked, hover }: { series: ChartSeries[]; geometry: Geometry; stacked: boolean; hover: number | null }) {
  const count = series[0]?.points.length ?? 0;
  const barWidth = Math.max(Math.min(geometry.band * 0.7, 48), 1);
  return (
    <>
      {Array.from({ length: count }, (_, i) => {
        let base = 0;
        return (
          <g key={i} opacity={hover === null || hover === i ? 1 : 0.55}>
            {series.map((s, index) => {
              const value = Math.max(s.points[i]?.value ?? 0, 0);
              if (value === 0) {
                return null;
              }

              const from = stacked ? base : 0;
              base = from + value;
              const top = geometry.y(from + value);
              return (
                <rect
                  key={s.key}
                  x={geometry.x(i) - barWidth / 2}
                  width={barWidth}
                  y={top}
                  height={Math.max(geometry.y(from) - top, 1)}
                  rx={index === series.length - 1 || !stacked ? 2 : 0}
                  fill={seriesColor(index, s.key)}
                />
              );
            })}
          </g>
        );
      })}
    </>
  );
}

function ChartTooltip({
  left,
  width,
  title,
  rows,
  total,
  unit,
  currency,
}: {
  left: number;
  width: number;
  title: string;
  rows: { key: string; label: string; color: string; value?: number | null }[];
  total?: number;
  unit: MetricUnit;
  currency?: string | null;
}) {
  const alignRight = left > width / 2;
  return (
    <div
      className='bg-popover text-popover-foreground pointer-events-none absolute top-0 z-10 min-w-40 rounded-md border px-3 py-2 text-xs shadow-md'
      style={alignRight ? { right: width - left + 12 } : { left: left + 12 }}
    >
      <p className='mb-1 font-medium'>{title}</p>
      <div className='space-y-0.5'>
        {rows.map((row) => (
          <div key={row.key} className='flex items-center gap-2'>
            <span className='size-2 shrink-0 rounded-full' style={{ background: row.color }} />
            <span className='text-muted-foreground max-w-40 truncate'>{row.label}</span>
            <span className='ml-auto pl-3 font-medium tabular-nums'>{formatMetric(row.value, unit, currency)}</span>
          </div>
        ))}
        {total !== undefined ? (
          <div className='flex items-center gap-2 border-t pt-1'>
            <span className='text-muted-foreground'>合计</span>
            <span className='ml-auto font-medium tabular-nums'>{formatMetric(total, unit, currency)}</span>
          </div>
        ) : null}
      </div>
    </div>
  );
}

/** 多组时的图例。 */
export function ChartLegend({ series, className }: { series: { key: string; label: string; total?: string }[]; className?: string }) {
  if (series.length <= 1) {
    return null;
  }

  return (
    <div className={cn('flex flex-wrap gap-x-4 gap-y-1 text-xs', className)}>
      {series.map((s, index) => (
        <span key={s.key} className='text-muted-foreground flex items-center gap-1.5'>
          <span className='size-2 rounded-full' style={{ background: seriesColor(index, s.key) }} />
          {s.label}
          {s.total ? <span className='text-foreground tabular-nums'>{s.total}</span> : null}
        </span>
      ))}
    </div>
  );
}
