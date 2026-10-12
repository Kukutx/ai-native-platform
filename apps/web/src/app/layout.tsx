import type { Metadata, Viewport } from 'next';
import { Providers } from '@/components/app/providers';
import { PRODUCT_NAME } from '@/lib/config';
import '@/styles/globals.css';

export const metadata: Metadata = {
  // 页面都是客户端组件，各页的标签页标题由 useDocumentTitle 在客户端设置；这里是默认值和服务端页面（例如 404）用的模板。
  title: { default: PRODUCT_NAME, template: `%s · ${PRODUCT_NAME}` },
  description: '与 AI 智能体团队一起工作：对话、任务、审批与自动执行。',
};

export const viewport: Viewport = {
  themeColor: [
    { media: '(prefers-color-scheme: light)', color: '#ffffff' },
    { media: '(prefers-color-scheme: dark)', color: '#0a0a0a' },
  ],
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang='zh-CN' data-theme='vercel' suppressHydrationWarning>
      <body className='min-h-svh antialiased'>
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
