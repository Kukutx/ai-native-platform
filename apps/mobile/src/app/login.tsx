import { useState } from 'react';
import { KeyboardAvoidingView, Platform, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { useMutation } from '@tanstack/react-query';
import { Button, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { auth, useAuth } from '@/lib/auth';
import { localTimeZone } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function LoginScreen() {
  const p = usePalette();
  const { baseUrl } = useAuth();
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [server, setServer] = useState(baseUrl);
  const [form, setForm] = useState({ email: '', password: '', displayName: '', tenantName: '' });
  const submit = useMutation({
    mutationFn: async () => {
      await auth.setBaseUrl(server);
      const api = getApi();
      return mode === 'login'
        ? api.auth.login({ email: form.email.trim(), password: form.password })
        : api.auth.register({
            email: form.email.trim(),
            password: form.password,
            displayName: form.displayName.trim(),
            tenantName: form.tenantName.trim() || `${form.displayName.trim()}的空间`,
            timeZone: localTimeZone(),
          });
    },
    onSuccess: (response) => auth.setToken(response.accessToken),
  });

  return (
    <SafeAreaView style={[styles.flex, { backgroundColor: p.background }]}>
      <KeyboardAvoidingView style={styles.flex} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
        <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
          <Text style={[styles.brand, { color: p.text }]}>✦ AI 原生工作台</Text>
          <Text style={[styles.title, { color: p.text }]}>{mode === 'login' ? '欢迎回来' : '创建工作空间'}</Text>
          <Muted>{mode === 'login' ? '登录后进入你的工作空间。' : '注册后会自动创建空间，并配好一位 AI 首席助理。'}</Muted>
          <View style={styles.form}>
            {mode === 'register' ? (
              <>
                <Field label='你的名字' value={form.displayName} onChangeText={(displayName) => setForm({ ...form, displayName })} placeholder='张三' />
                <Field label='空间名称' value={form.tenantName} onChangeText={(tenantName) => setForm({ ...form, tenantName })} placeholder='我的工作室' />
              </>
            ) : null}
            <Field
              label='邮箱'
              value={form.email}
              onChangeText={(email) => setForm({ ...form, email })}
              keyboardType='email-address'
              autoComplete='email'
              placeholder='you@example.com'
            />
            <Field
              label={mode === 'login' ? '密码' : '密码（至少 8 位）'}
              value={form.password}
              onChangeText={(password) => setForm({ ...form, password })}
              secureTextEntry
              autoComplete='password'
            />
            <Field label='服务器地址' value={server} onChangeText={setServer} keyboardType='url' placeholder='http://192.168.1.10:5080' />
            <Button
              title={mode === 'login' ? '登录' : '注册并进入'}
              onPress={() => submit.mutate()}
              loading={submit.isPending}
              disabled={!form.email || !form.password || (mode === 'register' && !form.displayName)}
            />
          </View>
          <Pressable onPress={() => setMode(mode === 'login' ? 'register' : 'login')} style={styles.switch}>
            <Muted>
              {mode === 'login' ? '还没有账号？' : '已有账号？'}
              <Text style={{ color: p.text, textDecorationLine: 'underline' }}>{mode === 'login' ? '注册' : '登录'}</Text>
            </Muted>
          </Pressable>
        </ScrollView>
      </KeyboardAvoidingView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 24, paddingTop: 48, gap: 8 },
  brand: { fontSize: 16, fontWeight: '600', marginBottom: 24 },
  title: { fontSize: 26, fontWeight: '700' },
  form: { gap: 14, marginTop: 20 },
  switch: { alignItems: 'center', marginTop: 20 },
});
