import { createApi, streamChat as streamChatRaw, type ApiConfig } from '@ainative/api-client';
import type { RealtimeClient } from '@ainative/api-client/realtime';
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

let realtime: Promise<RealtimeClient> | null = null;

/**
 * 实时通道客户端。SignalR 体积较大，按需加载、不进首屏脚本；整个页面共用一个连接。
 * 加载失败（例如部署更新后旧的分块已不存在）时不缓存失败结果，下次调用重新加载。
 */
export function loadRealtime() {
  realtime ??= import('@ainative/api-client/realtime')
    .then(({ RealtimeClient }) => new RealtimeClient({ baseUrl: API_URL, getToken: readToken }))
    .catch((error: unknown) => {
      realtime = null;
      throw error;
    });
  return realtime;
}
