// 从后端导出 OpenAPI 文档并生成 TypeScript 客户端：
//   1. 在内存中启动 API 宿主导出 packages/api-client/openapi.json（不需要数据库）；
//   2. 用 NSwag 生成 packages/api-client/src/generated.ts。
// 用法：pnpm api:generate（需要 .NET 10 SDK）。
import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const backend = resolve(root, 'backend');

function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', env: { ...process.env, DOTNET_NOLOGO: '1' } });
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

run('dotnet', ['tool', 'restore'], backend);
run('dotnet', ['run', '--project', 'AiNative.OpenApi.Export', '--', resolve(root, 'packages/api-client/openapi.json')], backend);
run('dotnet', ['tool', 'run', 'nswag', 'run', 'nswag.json'], backend);
