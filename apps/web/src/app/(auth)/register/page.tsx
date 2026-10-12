'use client';

import { AuthForm } from '@/components/app/auth-form';
import { api } from '@/lib/api';

export default function RegisterPage() {
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
      submit={(v) => api.auth.register({ displayName: v.displayName, tenantName: v.tenantName, email: v.email, password: v.password })}
      footer={{ text: '已有账号？', href: '/login', link: '登录' }}
    />
  );
}
