// 为 Web 静态导出预先生成 .br 和 .gz 压缩版本，后端按浏览器的 Accept-Encoding 直接发送（见 WebClientHosting）。
// 构建镜像时压缩一次、用最高压缩率，运行时不再占用 CPU。
// 用法：node tools/precompress.mjs apps/web/out
import { readdir, readFile, stat, writeFile } from 'node:fs/promises';
import { extname, join, resolve } from 'node:path';
import { brotliCompress, constants, gzip } from 'node:zlib';
import { promisify } from 'node:util';

const brotli = promisify(brotliCompress);
const gz = promisify(gzip);

// 与后端 WebClientHosting.Compressible 保持一致。
const extensions = new Set(['.html', '.js', '.css', '.txt', '.json', '.svg']);
// 太小的文件压缩收益抵不过请求头和解压开销。
const minimumSize = 1024;

const root = resolve(process.argv[2] ?? 'apps/web/out');

async function* walk(dir) {
  for (const entry of await readdir(dir, { withFileTypes: true })) {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) {
      yield* walk(path);
    } else if (extensions.has(extname(entry.name).toLowerCase())) {
      yield path;
    }
  }
}

let files = 0;
let original = 0;
let compressed = 0;
const pending = [];
for await (const path of walk(root)) {
  pending.push(
    (async () => {
      const { size } = await stat(path);
      if (size < minimumSize) {
        return;
      }

      const content = await readFile(path);
      const [br, gzipped] = await Promise.all([
        brotli(content, {
          params: {
            [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
            [constants.BROTLI_PARAM_SIZE_HINT]: size,
          },
        }),
        gz(content, { level: constants.Z_BEST_COMPRESSION }),
      ]);
      // 压不小的文件（例如已经压缩过的内容）不生成，后端会发送原文件。
      if (br.length < size * 0.9) {
        await writeFile(`${path}.br`, br);
      }
      if (gzipped.length < size * 0.9) {
        await writeFile(`${path}.gz`, gzipped);
      }

      files += 1;
      original += size;
      compressed += br.length;
    })(),
  );
}

await Promise.all(pending);
const kb = (bytes) => `${Math.round(bytes / 1024)} KB`;
console.log(`已压缩 ${files} 个文件：${kb(original)} → ${kb(compressed)}（br）`);
