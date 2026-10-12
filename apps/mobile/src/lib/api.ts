import { createApi, streamChat as streamChatRaw, type ApiConfig } from '@ainative/api-client';
import { fetch as expoFetch } from 'expo/fetch';
import { auth } from './auth';

function config(): ApiConfig {
  return {
    baseUrl: auth.get().baseUrl,
    getToken: () => auth.get().token,
    onUnauthorized: () => void auth.setToken(null),
  };
}

let cached: { baseUrl: string; api: ReturnType<typeof createApi> } | null = null;

/** 与 Web 共用的生成客户端；服务器地址可在登录页修改，变更后重新创建。 */
export function getApi() {
  const { baseUrl } = auth.get();
  if (cached?.baseUrl !== baseUrl) {
    cached = { baseUrl, api: createApi(config()) };
  }

  return cached.api;
}

/** 流式对话：React Native 自带的 fetch 不支持读取响应流，改用 expo/fetch。 */
export function streamChat(conversationId: string, text: string, signal?: AbortSignal) {
  return streamChatRaw({ ...config(), fetch: expoFetch as unknown as typeof fetch }, conversationId, text, signal);
}
