'use client';

import { SectionTabs, type SectionTab } from '@/components/app/section-tabs';

const tabs: SectionTab[] = [
  { href: '/crm', label: '商机', match: (path) => path === '/crm' || path.startsWith('/crm/deals') },
  { href: '/crm/companies', label: '客户', match: (path) => path.startsWith('/crm/companies') },
  { href: '/crm/contacts', label: '联系人', match: (path) => path.startsWith('/crm/contacts') },
];

/** 客户管理各页面顶部的切换条。 */
export function CrmNav() {
  return <SectionTabs tabs={tabs} />;
}
