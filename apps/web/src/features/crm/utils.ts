import type { ActorDto } from '@ainative/api-client';

/** 标签输入：逗号、顿号或空格分隔。 */
export function parseTags(text: string) {
  return [...new Set(text.split(/[,，、\s]+/).map((t) => t.trim()).filter(Boolean))];
}

/** 负责人是用户时取其 ID，供编辑时原样提交。 */
export function ownerUserId(owner?: ActorDto | null) {
  return owner?.kind === 'user' ? owner.id : undefined;
}

/** 空字符串转成 undefined，可选字段不提交空值。 */
export function blankToUndefined(value: string) {
  const trimmed = value.trim();
  return trimmed ? trimmed : undefined;
}

/** 预计成交日期已过但商机仍在进行中。 */
export function isOverdue(expectedCloseDate: string | undefined, status: string) {
  if (!expectedCloseDate || status !== 'open') {
    return false;
  }

  const today = new Date();
  const local = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`;
  return expectedCloseDate < local;
}
