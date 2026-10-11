/** 后端 API 地址。桌面端打包时同样通过构建期环境变量注入。 */
export const API_URL = process.env.NEXT_PUBLIC_API_URL ?? 'http://localhost:5080';
