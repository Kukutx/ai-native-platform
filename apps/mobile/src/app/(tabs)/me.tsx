import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import Ionicons from '@expo/vector-icons/Ionicons';
import { router, Stack } from 'expo-router';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Button, Card, Chip, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { auth, useAuth } from '@/lib/auth';
import { useCan, useSession } from '@/lib/session';
import { usePalette } from '@/lib/theme';

type Route = '/agents' | '/runs' | '/projects' | '/analytics' | '/erp' | '/workflows';

/** “我的”里的入口：没有权限或模块停用时不显示。 */
const entries: { feature: string; route: Route; icon: keyof typeof Ionicons.glyphMap; title: string; hint: string }[] = [
  { feature: 'agents.view', route: '/agents', icon: 'people-outline', title: '智能体', hint: '你的 AI 员工：状态、岗位职责和本月花费' },
  { feature: 'runs.view', route: '/runs', icon: 'pulse-outline', title: '执行记录', hint: '智能体每次醒来做了什么、花了多少钱' },
  { feature: 'projects.view', route: '/projects', icon: 'flag-outline', title: '项目', hint: '项目进度、里程碑和成员' },
  { feature: 'analytics.view', route: '/analytics', icon: 'bar-chart-outline', title: '经营分析', hint: '赢单、发货、任务完成和 AI 花费' },
  { feature: 'erp.view', route: '/erp', icon: 'cube-outline', title: '进销存', hint: '库存、销售订单和采购订单' },
  { feature: 'workflows.view', route: '/workflows', icon: 'flash-outline', title: '自动化工作流', hint: '查看、启停和手动运行工作流' },
];

export default function MeScreen() {
  const p = usePalette();
  const { baseUrl } = useAuth();
  const queryClient = useQueryClient();
  const session = useSession();
  const can = useCan();
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
          <Muted>
            当前空间：{session.data.tenant.name} · {session.data.tenant.timeZone}
          </Muted>
        </Card>
      ) : null}
      {entries
        .filter((entry) => can(entry.feature))
        .map((entry) => (
          <Pressable key={entry.route} onPress={() => router.push(entry.route)} accessibilityRole='button'>
            <Card>
              <View style={styles.entry}>
                <Ionicons name={entry.icon} size={20} color={p.text} />
                <View style={{ flex: 1 }}>
                  <Text style={{ color: p.text, fontWeight: '600' }}>{entry.title}</Text>
                  <Muted>{entry.hint}</Muted>
                </View>
                <Ionicons name='chevron-forward' size={18} color={p.muted} />
              </View>
            </Card>
          </Pressable>
        ))}
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
