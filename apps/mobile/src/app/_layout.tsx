import { useEffect, useState } from 'react';
import { Alert, AppState, Platform, useColorScheme } from 'react-native';
import { DarkTheme, DefaultTheme, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { focusManager, MutationCache, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { toProblem } from '@ainative/api-client';
import { auth, useAuth } from '@/lib/auth';
import { useRealtimeSync } from '@/lib/realtime';

// 回到前台时让查询重新获取，相当于 Web 端的窗口聚焦刷新。
function useAppStateFocus() {
  useEffect(() => {
    if (Platform.OS === 'web') {
      return;
    }

    const subscription = AppState.addEventListener('change', (status) => focusManager.setFocused(status === 'active'));
    return () => subscription.remove();
  }, []);
}

function RealtimeSync() {
  useRealtimeSync();
  return null;
}

export default function RootLayout() {
  const scheme = useColorScheme();
  const { ready, token } = useAuth();
  const [queryClient] = useState(
    () =>
      new QueryClient({
        defaultOptions: { queries: { staleTime: 15_000, retry: (count, error) => toProblem(error).status >= 500 && count < 2 } },
        mutationCache: new MutationCache({ onError: (error) => Alert.alert('操作失败', toProblem(error).title) }),
      }),
  );

  useAppStateFocus();
  useEffect(() => {
    void auth.hydrate();
  }, []);

  useEffect(() => {
    if (!token) {
      queryClient.clear();
    }
  }, [token, queryClient]);

  if (!ready) {
    return null;
  }

  return (
    <SafeAreaProvider>
      <QueryClientProvider client={queryClient}>
        <RealtimeSync />
        <ThemeProvider value={scheme === 'dark' ? DarkTheme : DefaultTheme}>
          <Stack screenOptions={{ headerBackButtonDisplayMode: 'minimal' }}>
            <Stack.Protected guard={!!token}>
              <Stack.Screen name='(tabs)' options={{ headerShown: false }} />
              <Stack.Screen name='chat/[id]' options={{ title: '对话' }} />
              <Stack.Screen name='tasks/[id]' options={{ title: '任务' }} />
              <Stack.Screen name='tasks/new' options={{ title: '新建任务', presentation: 'modal' }} />
              <Stack.Screen name='knowledge/[id]' options={{ title: '文档' }} />
              <Stack.Screen name='workflows/index' options={{ title: '自动化' }} />
              <Stack.Screen name='workflows/[id]' options={{ title: '工作流' }} />
              <Stack.Screen name='workflows/runs/[id]' options={{ title: '运行详情' }} />
              <Stack.Screen name='crm/deals/[id]' options={{ title: '商机' }} />
              <Stack.Screen name='crm/companies/[id]' options={{ title: '客户' }} />
              <Stack.Screen name='crm/contacts/[id]' options={{ title: '联系人' }} />
              <Stack.Screen name='crm/new' options={{ title: '新建', presentation: 'modal' }} />
              <Stack.Screen name='erp/index' options={{ title: '进销存' }} />
              <Stack.Screen name='erp/sales-orders/[id]' options={{ title: '销售订单' }} />
              <Stack.Screen name='erp/purchase-orders/[id]' options={{ title: '采购订单' }} />
              <Stack.Screen name='erp/products/[id]' options={{ title: '产品' }} />
              <Stack.Screen name='projects/index' options={{ title: '项目' }} />
              <Stack.Screen name='projects/[id]' options={{ title: '项目' }} />
              <Stack.Screen name='analytics/index' options={{ title: '经营分析' }} />
            </Stack.Protected>
            <Stack.Protected guard={!token}>
              <Stack.Screen name='login' options={{ headerShown: false }} />
            </Stack.Protected>
          </Stack>
          <StatusBar style='auto' />
        </ThemeProvider>
      </QueryClientProvider>
    </SafeAreaProvider>
  );
}
