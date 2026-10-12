/** 本地日期 yyyy-MM-dd（不做时区换算），用作日期输入框的默认值或与只有日期的值比较。 */
export function today(now = new Date()) {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`;
}

/** 本机时区（IANA 名称），注册空间、定时工作流时作为默认时区；取不到时返回 undefined。 */
export function localTimeZone() {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || undefined;
  } catch {
    return undefined;
  }
}
