import type { ApiConfig } from './http';
import { createHttp } from './http';

/** 流式对话事件（与后端 ChatStreamEvent 对应）。 */
export interface ChatStreamEvent {
  type: 'text_delta' | 'tool_call' | 'tool_result' | 'done' | 'error';
  text?: string;
  callId?: string;
  actionKey?: string;
  data?: unknown;
  messageId?: string;
}

/**
 * 通过 Server-Sent Events 发送消息并逐个产出事件。Web 直接可用；Expo 需在 ApiConfig.fetch 传入 expo/fetch。
 */
export async function* streamChat(
  config: ApiConfig,
  conversationId: string,
  text: string,
  signal?: AbortSignal,
): AsyncGenerator<ChatStreamEvent> {
  const http = createHttp(config);
  const response = await http.fetch(`${config.baseUrl.replace(/\/+$/, '')}/api/conversations/${conversationId}/messages`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify({ text }),
    signal,
  });

  if (!response.ok || !response.body) {
    let title = `请求失败（${response.status}）`;
    try {
      title = ((await response.json()) as { title?: string }).title ?? title;
    } catch {
      // 非 JSON 错误体，保留默认提示。
    }

    yield { type: 'error', text: title };
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { value, done } = await reader.read();
    if (done) {
      break;
    }

    buffer += decoder.decode(value, { stream: true });
    let boundary = buffer.indexOf('\n\n');
    while (boundary >= 0) {
      const frame = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      const data = frame
        .split('\n')
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trimStart())
        .join('\n');
      if (data) {
        yield JSON.parse(data) as ChatStreamEvent;
      }

      boundary = buffer.indexOf('\n\n');
    }
  }
}
