import { createApi, streamChat as streamChatRaw, type ApiConfig } from '@ainative/api-client';
import { RealtimeClient } from '@ainative/api-client/realtime';
import { API_URL } from './config';
import { readToken, writeToken } from './token';

const config: ApiConfig = {
  baseUrl: API_URL,
  getToken: readToken,
  onUnauthorized: () => writeToken(null),
};

/** 全部 HTTP 接口都从这里走（类型来自后端 OpenAPI 生成）。 */
export const api = createApi(config);

export function streamChat(conversationId: string, text: string, signal?: AbortSignal) {
  return streamChatRaw(config, conversationId, text, signal);
}

let realtime: RealtimeClient | null = null;

export function getRealtime() {
  realtime ??= new RealtimeClient({ baseUrl: API_URL, getToken: readToken });
  return realtime;
}
