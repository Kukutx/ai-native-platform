import { useSyncExternalStore } from 'react';
import { Platform } from 'react-native';
import * as SecureStore from 'expo-secure-store';

const TOKEN_KEY = 'ainative.token';
const SERVER_KEY = 'ainative.server';

export const DEFAULT_API_URL = process.env.EXPO_PUBLIC_API_URL ?? 'http://localhost:5080';

interface AuthState {
  ready: boolean;
  token: string | null;
  baseUrl: string;
}

/** 原生端用系统安全存储；Web 预览没有 Keychain，退回 localStorage。 */
const storage =
  Platform.OS === 'web'
    ? {
        getItemAsync: async (key: string) => globalThis.localStorage?.getItem(key) ?? null,
        setItemAsync: async (key: string, value: string) => globalThis.localStorage?.setItem(key, value),
        deleteItemAsync: async (key: string) => globalThis.localStorage?.removeItem(key),
      }
    : SecureStore;

let state: AuthState = { ready: false, token: null, baseUrl: DEFAULT_API_URL };
const listeners = new Set<() => void>();

function emit(next: Partial<AuthState>) {
  state = { ...state, ...next };
  listeners.forEach((listener) => listener());
}

/**
 * 登录状态：令牌和服务器地址保存在系统安全存储（Keychain / Keystore），内存里保留一份供同步读取。
 */
export const auth = {
  get: () => state,
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  async hydrate() {
    const [token, baseUrl] = await Promise.all([storage.getItemAsync(TOKEN_KEY), storage.getItemAsync(SERVER_KEY)]);
    emit({ ready: true, token, baseUrl: baseUrl ?? DEFAULT_API_URL });
  },
  async setToken(token: string | null) {
    emit({ token });
    if (token) {
      await storage.setItemAsync(TOKEN_KEY, token);
    } else {
      await storage.deleteItemAsync(TOKEN_KEY);
    }
  },
  async setBaseUrl(baseUrl: string) {
    const normalized = baseUrl.trim().replace(/\/+$/, '') || DEFAULT_API_URL;
    emit({ baseUrl: normalized });
    await storage.setItemAsync(SERVER_KEY, normalized);
  },
};

export function useAuth() {
  return useSyncExternalStore(auth.subscribe, auth.get, auth.get);
}
