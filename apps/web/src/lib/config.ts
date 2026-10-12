/**
 * 后端 API 地址。构建期注入：桌面端打包时填后端的完整地址；
 * 生产镜像里留空，页面和接口同源，所有请求都用相对地址。
 */
export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5080';

/** 给外部工具（例如 MCP 客户端）用的完整地址；同源部署时取当前页面的域名。只能在浏览器里调用。 */
export function absoluteApiUrl(path: string) {
  return `${(API_URL || window.location.origin).replace(/\/+$/, '')}${path}`;
}
