import type { SessionDto } from './generated';

/** 与后端 FeatureMatcher 相同的规则：精确、模块通配（tasks.*）、全部（*）。 */
export function isGranted(features: readonly string[], feature: string) {
  return features.some((grant) => grant === '*' || grant === feature || (grant.endsWith('.*') && feature.startsWith(grant.slice(0, -1))));
}

/**
 * 当前会话能否使用某项功能。角色授予了、但所属模块在当前空间停用的功能，后端同样会拒绝，界面上也不显示入口。
 */
export function sessionCan(session: SessionDto | undefined, feature: string) {
  return !!session && !session.disabledFeatures.includes(feature) && isGranted(session.features, feature);
}
