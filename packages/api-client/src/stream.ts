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
 * 个别无法解析的事件会被跳过，不会中断整轮对话。
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

  for await (const data of readSseData(response.body)) {
    const event = parseEvent(data);
    if (event) {
      yield event;
    }
  }
}

/**
 * 从 SSE 字节流中逐个取出事件的 data 字段（同一事件的多行 data 按规范以换行拼接）。
 * 流结束时最后一个没有以空行收尾的事件也会产出；注释、event、id 等其他字段忽略。
 * 调用方提前结束遍历时取消底层的流，释放连接。
 */
export async function* readSseData(body: ReadableStream<Uint8Array>): AsyncGenerator<string> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  let finished = false;
  try {
    while (!finished) {
      const { value, done } = await reader.read();
      finished = done;
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      // 行尾可以是 \r\n、\n 或单独的 \r。末尾的 \r 可能和下一块开头的 \n 是一对，留到下一块再处理。
      buffer = buffer.replace(done ? /\r\n?/g : /\r\n|\r(?!$)/g, '\n');

      let boundary = buffer.indexOf('\n\n');
      while (boundary >= 0) {
        const data = frameData(buffer.slice(0, boundary));
        buffer = buffer.slice(boundary + 2);
        if (data !== undefined) {
          yield data;
        }

        boundary = buffer.indexOf('\n\n');
      }
    }

    const last = frameData(buffer);
    if (last !== undefined) {
      yield last;
    }
  } finally {
    if (!finished) {
      await reader.cancel().catch(() => undefined);
    }

    reader.releaseLock();
  }
}

/** 一个事件块里的 data 字段；没有 data 行时返回 undefined。 */
function frameData(frame: string) {
  const lines = frame
    .split('\n')
    .filter((line) => line.startsWith('data:'))
    .map((line) => (line.startsWith('data: ') ? line.slice(6) : line.slice(5)));
  return lines.length > 0 ? lines.join('\n') : undefined;
}

function parseEvent(data: string): ChatStreamEvent | undefined {
  try {
    const event = JSON.parse(data) as unknown;
    return typeof event === 'object' && event !== null && typeof (event as { type?: unknown }).type === 'string'
      ? (event as ChatStreamEvent)
      : undefined;
  } catch {
    return undefined;
  }
}
