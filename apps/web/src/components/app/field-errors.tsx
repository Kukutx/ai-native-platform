import { toProblem } from '@ainative/api-client';

export type FieldErrors = Record<string, string[]>;

/** 接口返回的字段错误；没有时返回空对象（总体错误由全局提示显示）。 */
export function fieldErrorsOf(error: unknown): FieldErrors {
  return error ? (toProblem(error).errors ?? {}) : {};
}

/** 取某个字段的第一条错误，子项的错误也算在内（例如 headers[0].Value 归到 headers）。 */
export function fieldError(errors: FieldErrors, field: string) {
  const target = field.toLowerCase();
  for (const [key, messages] of Object.entries(errors)) {
    const name = key.replace(/^\./, '').toLowerCase();
    if (name === target || name.startsWith(`${target}[`) || name.startsWith(`${target}.`)) {
      return messages[0];
    }
  }

  return undefined;
}

export function FieldMessage({ message }: { message?: string }) {
  return message ? <p className='text-destructive text-xs'>{message}</p> : null;
}
