import { ScrollView, StyleSheet, Text } from 'react-native';
import { Stack } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Chip, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { auth, useAuth } from '@/lib/auth';
import { usePalette } from '@/lib/theme';

export default function MeScreen() {
  const p = usePalette();
  const { baseUrl } = useAuth();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const switchTenant = useMutation({
    mutationFn: (tenantId: string) => getApi().auth.switchTenant({ tenantId }),
    onSuccess: async (response) => {
      await auth.setToken(response.accessToken);
      queryClient.clear();
    },
  });

  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container}>
      <Stack.Screen options={{ title: '我的' }} />
      {session.data ? (
        <Card>
          <Text style={{ color: p.text, fontSize: 18, fontWeight: '600' }}>{session.data.user.displayName}</Text>
          <Muted>{session.data.user.email}</Muted>
          <Muted>当前空间：{session.data.tenant.name}</Muted>
        </Card>
      ) : null}
      {session.data && session.data.tenants.length > 1 ? (
        <Card>
          <Text style={{ color: p.text, fontWeight: '600' }}>切换空间</Text>
          {session.data.tenants.map((t) => (
            <Chip key={t.id} label={t.name} active={t.id === session.data.tenant.id} onPress={() => switchTenant.mutate(t.id)} />
          ))}
        </Card>
      ) : null}
      <Card>
        <Text style={{ color: p.text, fontWeight: '600' }}>服务器</Text>
        <Muted>{baseUrl}</Muted>
      </Card>
      <Button title='退出登录' variant='danger' onPress={() => void auth.setToken(null)} />
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { padding: 16, gap: 12 },
});
