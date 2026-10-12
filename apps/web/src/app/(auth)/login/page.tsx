'use client';

import { AuthForm } from '@/components/app/auth-form';
import { useAuthOptions } from '@/features/auth/use-auth-options';
import { api } from '@/lib/api';

export default function LoginPage() {
  const options = useAuthOptions();
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
      // 站点关闭注册时不显示注册入口；设置还没读到时也先不显示，避免闪一下又消失。
      footer={options.data?.allowRegistration ? { text: '还没有账号？', href: '/register', link: '注册' } : undefined}
    />
  );
}
