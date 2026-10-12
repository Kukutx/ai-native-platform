'use client';

import { SectionTabs, type SectionTab } from '@/components/app/section-tabs';

const tabs: SectionTab[] = [
  { href: '/erp', label: '概览', match: (path) => path === '/erp' },
  { href: '/erp/sales-orders', label: '销售订单', match: (path) => path.startsWith('/erp/sales-orders') },
  { href: '/erp/purchase-orders', label: '采购订单', match: (path) => path.startsWith('/erp/purchase-orders') },
  { href: '/erp/inventory', label: '库存', match: (path) => path.startsWith('/erp/inventory') },
  { href: '/erp/products', label: '产品', match: (path) => path.startsWith('/erp/products') },
];

/** 进销存各页面顶部的切换条。 */
export function ErpNav() {
  return <SectionTabs tabs={tabs} />;
}
