'use client';

import { AuthForm } from '@/components/app/auth-form';
import { api } from '@/lib/api';

export default function LoginPage() {
  return (
    <AuthForm
      title='欢迎回来'
      description='登录后进入你的工作空间。'
      submitLabel='登录'
      fields={[
        { name: 'email', label: '邮箱', type: 'email', placeholder: 'you@example.com', autoComplete: 'email' },
        { name: 'password', label: '密码', type: 'password', autoComplete: 'current-password' },
      ]}
      submit={(v) => api.auth.login({ email: v.email, password: v.password })}
      footer={{ text: '还没有账号？', href: '/register', link: '注册' }}
    />
  );
}
