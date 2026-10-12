import Ionicons from '@expo/vector-icons/Ionicons';
import type { ColorValue } from 'react-native';
import { useQuery } from '@tanstack/react-query';
import { Tabs } from 'expo-router/js-tabs';
import { getApi } from '@/lib/api';
import { usePalette } from '@/lib/theme';

type IconName = keyof typeof Ionicons.glyphMap;

function icon(name: IconName) {
  return function TabIcon({ color, size }: { color: ColorValue; size: number }) {
    return <Ionicons name={name} color={color} size={size} />;
  };
}

export default function TabsLayout() {
  const p = usePalette();
  const pending = useQuery({ queryKey: ['approvals', 'pending'], queryFn: ({ signal }) => getApi().approvals.list('pending', signal) });

  return (
    <Tabs screenOptions={{ tabBarActiveTintColor: p.text, tabBarInactiveTintColor: p.muted }}>
      <Tabs.Screen name='index' options={{ title: '对话', tabBarIcon: icon('chatbubbles-outline') }} />
      <Tabs.Screen name='tasks' options={{ title: '任务', tabBarIcon: icon('checkmark-done-outline') }} />
      <Tabs.Screen name='knowledge' options={{ title: '知识', tabBarIcon: icon('library-outline') }} />
      <Tabs.Screen
        name='approvals'
        options={{ title: '审批', tabBarIcon: icon('shield-checkmark-outline'), tabBarBadge: pending.data?.length || undefined }}
      />
      <Tabs.Screen name='me' options={{ title: '我的', tabBarIcon: icon('person-circle-outline') }} />
    </Tabs>
  );
}
