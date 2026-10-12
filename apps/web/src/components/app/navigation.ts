import { Icons } from '@/components/icons';

export interface NavigationEntry {
  href: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  /** 进入这个入口需要的功能权限；所属模块停用时同样不可用（见 sessionCan）。 */
  feature: string;
}

/** 工作台的一级入口：侧栏导航、页面权限检查和登录后的默认页都以它为准。 */
export const navigation: NavigationEntry[] = [
  { href: '/chat', label: '对话', icon: Icons.chat, feature: 'chat.use' },
  { href: '/agents', label: '智能体', icon: Icons.teams, feature: 'agents.view' },
  { href: '/tasks', label: '任务', icon: Icons.checks, feature: 'tasks.view' },
  { href: '/projects', label: '项目', icon: Icons.project, feature: 'projects.view' },
  { href: '/crm', label: '客户', icon: Icons.crm, feature: 'crm.view' },
  { href: '/erp', label: '进销存', icon: Icons.product, feature: 'erp.view' },
  { href: '/knowledge', label: '知识库', icon: Icons.knowledge, feature: 'knowledge.view' },
  { href: '/analytics', label: '经营分析', icon: Icons.analytics, feature: 'analytics.view' },
  { href: '/workflows', label: '自动化', icon: Icons.workflow, feature: 'workflows.view' },
  { href: '/integrations', label: '集成', icon: Icons.integration, feature: 'integrations.view' },
  { href: '/runs', label: '执行记录', icon: Icons.clock, feature: 'runs.view' },
  { href: '/approvals', label: '审批', icon: Icons.badgeCheck, feature: 'approvals.view' },
  { href: '/settings', label: '设置', icon: Icons.settings, feature: 'platform.members.view' },
];

/** 路径所属的一级入口（例如 /crm/deals/view 属于「客户」）；不属于任何入口时返回 undefined。 */
export function navigationEntryFor(pathname: string) {
  // 静态导出后路径可能带结尾斜杠。
  const path = pathname.replace(/\/+$/, '') || '/';
  return navigation.find((entry) => path === entry.href || path.startsWith(`${entry.href}/`));
}

/** 当前身份可以进入的第一个入口，作为首页和登录后的默认页。 */
export function firstAllowedEntry(can: (feature: string) => boolean) {
  return navigation.find((entry) => can(entry.feature));
}
