'use client';

import Link from 'next/link';
import { useMutation } from '@tanstack/react-query';
import type { AuthResponse } from '@ainative/api-client';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Spinner } from '@/components/ui/spinner';
import { useSession } from './session-provider';

interface FieldSpec {
  name: string;
  label: string;
  type?: string;
  placeholder?: string;
  autoComplete?: string;
}

/** 登录、注册共用的表单：提交后拿到令牌即进入工作台。 */
export function AuthForm({
  title,
  description,
  fields,
  submitLabel,
  submit,
  footer,
}: {
  title: string;
  description: string;
  fields: FieldSpec[];
  submitLabel: string;
  submit: (values: Record<string, string>) => Promise<AuthResponse>;
  /** 不传则不显示底部链接（例如站点关闭了注册）。 */
  footer?: { text: string; href: string; link: string };
}) {
  const { signIn } = useSession();
  const mutation = useMutation({ mutationFn: submit, onSuccess: signIn });

  return (
    <Card>
      <CardHeader>
        <CardTitle className='text-xl'>{title}</CardTitle>
        <CardDescription>{description}</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className='grid gap-4'
          onSubmit={(event) => {
            event.preventDefault();
            const data = new FormData(event.currentTarget);
            mutation.mutate(Object.fromEntries(fields.map((f) => [f.name, String(data.get(f.name) ?? '')])));
          }}
        >
          {fields.map((field) => (
            <div key={field.name} className='grid gap-2'>
              <Label htmlFor={field.name}>{field.label}</Label>
              <Input
                id={field.name}
                name={field.name}
                type={field.type ?? 'text'}
                placeholder={field.placeholder}
                autoComplete={field.autoComplete}
                required
              />
            </div>
          ))}
          <Button type='submit' disabled={mutation.isPending}>
            {mutation.isPending ? <Spinner /> : null}
            {submitLabel}
          </Button>
          {footer ? (
            <p className='text-muted-foreground text-center text-sm'>
              {footer.text}{' '}
              <Link href={footer.href} className='text-foreground underline underline-offset-4'>
                {footer.link}
              </Link>
            </p>
          ) : null}
        </form>
      </CardContent>
    </Card>
  );
}
