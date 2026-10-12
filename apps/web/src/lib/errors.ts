import { toProblem } from '@ainative/api-client';

/**
 * 给用户看的错误说明：后端返回的中文提示；连不上服务器，或者返回的不是平台的标准错误（例如网关报错）时给出通用说明，
 * 不把生成客户端的英文异常信息直接展示出来。
 */
export function errorMessage(error: unknown) {
  const problem = toProblem(error);
  if (problem.status === 0) {
    return '无法连接服务器，请检查网络后重试。';
  }

  if (problem.code === 'unknown') {
    if (problem.status === 403) {
      return '没有权限执行这个操作。';
    }

    if (problem.status === 404) {
      return '要访问的内容不存在或已被删除。';
    }

    return problem.status >= 500 ? `服务器出错了（${problem.status}），请稍后重试。` : `请求失败（${problem.status}），请稍后重试。`;
  }

  return problem.title;
}

/** 写操作失败时的提示：参数校验失败时列出前几条具体原因，否则同 errorMessage。 */
export function mutationErrorMessage(error: unknown) {
  const reasons = Object.values(toProblem(error).errors ?? {}).flat();
  return reasons.length ? reasons.slice(0, 3).join(' ') : errorMessage(error);
}
