import type { NextConfig } from 'next';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = dirname(fileURLToPath(import.meta.url));

/**
 * 工作台是纯客户端应用（数据都来自 API），因此可以静态导出：
 * NEXT_OUTPUT=export 时输出到 out/，供 Tauri 桌面端打包；默认模式用于 Web 部署与开发。
 */
const nextConfig: NextConfig = {
  poweredByHeader: false,
  output: process.env.NEXT_OUTPUT === 'export' ? 'export' : undefined,
  images: { unoptimized: true },
  turbopack: {
    root: resolve(webRoot, '../..'),
  },
  transpilePackages: ['@ainative/api-client'],
};

export default nextConfig;
