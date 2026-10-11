import type { Metadata, Viewport } from 'next';
import { Providers } from '@/components/app/providers';
import '@/styles/globals.css';

export const metadata: Metadata = {
  title: { default: 'AI 原生工作台', template: '%s · AI 原生工作台' },
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
