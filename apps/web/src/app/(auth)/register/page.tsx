'use client';

import Link from 'next/link';
import { AuthForm } from '@/components/app/auth-form';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { useAuthOptions } from '@/features/auth/use-auth-options';
import { api } from '@/lib/api';
import { localTimeZone } from '@/lib/labels';

export default function RegisterPage() {
  const options = useAuthOptions();
  if (options.data?.allowRegistration === false) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className='text-xl'>暂不开放注册</CardTitle>
          <CardDescription>这个站点已关闭自助注册。请联系管理员把你加入已有空间，然后用管理员告诉你的邮箱和密码登录。</CardDescription>
        </CardHeader>
        <CardContent>
          <Button asChild className='w-full'>
            <Link href='/login'>去登录</Link>
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <AuthForm
      title='创建工作空间'
      description='注册后会自动创建空间，并配好一位 AI 首席助理。'
      submitLabel='注册并进入'
      fields={[
        { name: 'displayName', label: '你的名字', placeholder: '张三', autoComplete: 'name' },
        { name: 'tenantName', label: '空间名称', placeholder: '我的工作室' },
        { name: 'email', label: '邮箱', type: 'email', placeholder: 'you@example.com', autoComplete: 'email' },
        { name: 'password', label: '密码（至少 8 位）', type: 'password', autoComplete: 'new-password' },
      ]}
      submit={(v) =>
        api.auth.register({ displayName: v.displayName, tenantName: v.tenantName, email: v.email, password: v.password, timeZone: localTimeZone() })
      }
      footer={{ text: '已有账号？', href: '/login', link: '登录' }}
    />
  );
}
