import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import Ionicons from '@expo/vector-icons/Ionicons';
import { router, Stack } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
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
      {sessionCan(session.data, 'projects.view') ? (
        <Pressable onPress={() => router.push('/projects')} accessibilityRole='button'>
          <Card>
            <View style={styles.entry}>
              <Ionicons name='flag-outline' size={20} color={p.text} />
              <View style={{ flex: 1 }}>
                <Text style={{ color: p.text, fontWeight: '600' }}>项目</Text>
                <Muted>项目进度、里程碑和成员</Muted>
              </View>
              <Ionicons name='chevron-forward' size={18} color={p.muted} />
            </View>
          </Card>
        </Pressable>
      ) : null}
      {sessionCan(session.data, 'erp.view') ? (
        <Pressable onPress={() => router.push('/erp')} accessibilityRole='button'>
          <Card>
            <View style={styles.entry}>
              <Ionicons name='cube-outline' size={20} color={p.text} />
              <View style={{ flex: 1 }}>
                <Text style={{ color: p.text, fontWeight: '600' }}>进销存</Text>
                <Muted>库存、销售订单和采购订单</Muted>
              </View>
              <Ionicons name='chevron-forward' size={18} color={p.muted} />
            </View>
          </Card>
        </Pressable>
      ) : null}
      {sessionCan(session.data, 'workflows.view') ? (
        <Pressable onPress={() => router.push('/workflows')} accessibilityRole='button'>
          <Card>
            <View style={styles.entry}>
              <Ionicons name='flash-outline' size={20} color={p.text} />
              <View style={{ flex: 1 }}>
                <Text style={{ color: p.text, fontWeight: '600' }}>自动化工作流</Text>
                <Muted>查看、启停和手动运行工作流</Muted>
              </View>
              <Ionicons name='chevron-forward' size={18} color={p.muted} />
            </View>
          </Card>
        </Pressable>
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
  entry: { flexDirection: 'row', alignItems: 'center', gap: 12 },
});
