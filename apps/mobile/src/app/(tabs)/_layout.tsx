import Ionicons from '@expo/vector-icons/Ionicons';
import { ActivityIndicator, StyleSheet, Text, View, type ColorValue } from 'react-native';
import { useQuery } from '@tanstack/react-query';
import { sessionCan, toProblem } from '@ainative/api-client';
import { Tabs } from 'expo-router/js-tabs';
import { Button, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { auth, useAuth } from '@/lib/auth';
import { tabFeatures, useSession, type GatedTab } from '@/lib/session';
import { usePalette } from '@/lib/theme';

type IconName = keyof typeof Ionicons.glyphMap;

function icon(name: IconName) {
  return function TabIcon({ color, size }: { color: ColorValue; size: number }) {
    return <Ionicons name={name} color={color} size={size} />;
  };
}

export default function TabsLayout() {
  const p = usePalette();
  const session = useSession();
  const can = (tab: GatedTab) => sessionCan(session.data, tabFeatures[tab]);
  const pending = useQuery({
    queryKey: ['approvals', 'pending'],
    queryFn: ({ signal }) => getApi().approvals.list('pending', signal),
    enabled: can('approvals'),
  });

  // 先拿到权限再渲染标签：没有权限的标签不会先出现再消失，也不会先打开一个必然 403 的页面。
  if (!session.data) {
    return <SessionPending error={session.isError ? toProblem(session.error).title : null} retrying={session.isFetching} onRetry={() => void session.refetch()} />;
  }

  // 没有权限或模块停用时隐藏对应的标签（href 为 null）。
  const href = (tab: GatedTab) => (can(tab) ? undefined : null);

  return (
    <Tabs screenOptions={{ tabBarActiveTintColor: p.text, tabBarInactiveTintColor: p.muted }}>
      <Tabs.Screen name='index' options={{ title: '对话', tabBarIcon: icon('chatbubbles-outline'), href: href('index') }} />
      <Tabs.Screen name='tasks' options={{ title: '任务', tabBarIcon: icon('checkmark-done-outline'), href: href('tasks') }} />
      <Tabs.Screen name='crm' options={{ title: '客户', tabBarIcon: icon('briefcase-outline'), href: href('crm') }} />
      <Tabs.Screen name='knowledge' options={{ title: '知识', tabBarIcon: icon('library-outline'), href: href('knowledge') }} />
      <Tabs.Screen
        name='approvals'
        options={{ title: '审批', tabBarIcon: icon('shield-checkmark-outline'), tabBarBadge: pending.data?.length || undefined, href: href('approvals') }}
      />
      <Tabs.Screen name='me' options={{ title: '我的', tabBarIcon: icon('person-circle-outline') }} />
    </Tabs>
  );
}

/** 会话加载中，或者连不上服务器时给出重试和退出登录（例如服务器地址填错了）。 */
function SessionPending({ error, retrying, onRetry }: { error: string | null; retrying: boolean; onRetry: () => void }) {
  const p = usePalette();
  const { baseUrl } = useAuth();
  if (!error) {
    return (
      <View style={[styles.center, { backgroundColor: p.background }]}>
        <ActivityIndicator color={p.muted} />
      </View>
    );
  }

  return (
    <View style={[styles.center, { backgroundColor: p.background }]}>
      <Text style={{ color: p.text, fontSize: 16, fontWeight: '600' }}>连不上服务器</Text>
      <Muted style={styles.hint}>
        {error}
        {'\n'}
        {baseUrl}
      </Muted>
      <View style={styles.actions}>
        <Button title='重试' onPress={onRetry} loading={retrying} />
        <Button title='退出登录' variant='outline' onPress={() => void auth.setToken(null)} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  hint: { marginTop: 8, textAlign: 'center', lineHeight: 20 },
  actions: { alignSelf: 'stretch', gap: 10, marginTop: 20 },
});
