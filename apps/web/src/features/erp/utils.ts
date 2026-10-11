/** 数字输入框的值：空白为 undefined，可选字段不提交空值。 */
export function toNumber(value: string) {
  const trimmed = value.trim();
  return trimmed ? Number(trimmed) : undefined;
}

/** 本地日期 yyyy-MM-dd，作为日期输入框的默认值。 */
export function today() {
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}
