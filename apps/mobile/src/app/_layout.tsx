import { useEffect, useState } from 'react';
import { Alert, AppState, Platform, useColorScheme } from 'react-native';
import { DarkTheme, DefaultTheme, Stack, ThemeProvider } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { focusManager, MutationCache, onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query';
import * as Network from 'expo-network';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { toProblem } from '@ainative/api-client';
import { auth, useAuth } from '@/lib/auth';
import { useRealtimeSync } from '@/lib/realtime';

/**
 * 只有明确没有网络（NONE）时才算离线。不看能否访问互联网：内网部署的服务器在没有外网的 Wi-Fi 下也要能用；
 * 也不用 isConnected：安卓上识别不了的连接方式（USB 共享等）会报 UNKNOWN，isConnected 为 false，但其实连着网。
 */
function isOnline(state: Network.NetworkState) {
  return state.type !== Network.NetworkStateType.NONE;
}

// 断网时暂停查询和变更，恢复连接后自动继续，而不是一路报错（Web 预览沿用浏览器的 online/offline 事件）。
if (Platform.OS !== 'web') {
  onlineManager.setEventListener((setOnline) => {
    let changed = false;
    const subscription = Network.addNetworkStateListener((state) => {
      changed = true;
      setOnline(isOnline(state));
    });
    Network.getNetworkStateAsync()
      .then((state) => {
        if (!changed) {
          setOnline(isOnline(state));
        }
      })
      .catch(() => {
        // 取不到网络状态时按在线处理。
      });
    return () => subscription.remove();
  });
}

/** 网络错误（status 0）和服务端错误（≥500）重试两次；4xx 是确定的结果，不重试。 */
function shouldRetry(failureCount: number, error: unknown) {
  const { status } = toProblem(error);
  return (status === 0 || status >= 500) && failureCount < 2;
}

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
        defaultOptions: { queries: { staleTime: 15_000, retry: shouldRetry } },
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
              <Stack.Screen name='agents/index' options={{ title: '智能体' }} />
              <Stack.Screen name='agents/[id]' options={{ title: '智能体' }} />
              <Stack.Screen name='runs/index' options={{ title: '执行记录' }} />
              <Stack.Screen name='runs/[id]' options={{ title: '执行详情' }} />
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
