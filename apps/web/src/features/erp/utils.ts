/** 数字输入框的值：空白为 undefined，可选字段不提交空值。 */
export function toNumber(value: string) {
  const trimmed = value.trim();
  return trimmed ? Number(trimmed) : undefined;
}
