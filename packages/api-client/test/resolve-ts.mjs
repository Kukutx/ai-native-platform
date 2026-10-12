// 单元测试直接用 Node 运行 TypeScript 源码（类型剥离）。源码按打包工具的习惯省略了扩展名（'./http'），
// Node 的 ESM 解析要求写全，这里在找不到时补上 .ts 再试一次。只用于测试，不影响 Web、移动端的打包。
import { registerHooks } from 'node:module';

registerHooks({
  resolve(specifier, context, nextResolve) {
    if ((specifier.startsWith('./') || specifier.startsWith('../')) && !/\.[cm]?[jt]s$/.test(specifier)) {
      try {
        return nextResolve(`${specifier}.ts`, context);
      } catch {
        // 不是省略了扩展名的 TS 源文件，按原样解析。
      }
    }

    return nextResolve(specifier, context);
  },
});
