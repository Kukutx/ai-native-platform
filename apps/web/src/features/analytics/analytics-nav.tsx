'use client';

import { SectionTabs } from '@/components/app/section-tabs';

const tabs = [
  { href: '/analytics', label: '概览', match: (path: string) => path === '/analytics' },
  { href: '/analytics/reports', label: '报表', match: (path: string) => path.startsWith('/analytics/reports') },
  { href: '/analytics/explore', label: '自由分析', match: (path: string) => path.startsWith('/analytics/explore') },
];

export function AnalyticsNav() {
  return <SectionTabs tabs={tabs} />;
}
