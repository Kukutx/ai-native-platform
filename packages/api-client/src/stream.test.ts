import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { ApiConfig } from './http';
import { readSseData, streamChat, type ChatStreamEvent } from './stream';

const encoder = new TextEncoder();

/** 把字节按固定大小切块逐块吐出，模拟网络把事件（以及一个汉字的多个字节）拆到不同的块里。 */
function chunked(text: string, size: number, onCancel?: () => void) {
  const bytes = encoder.encode(text);
  let offset = 0;
  return new ReadableStream<Uint8Array>({
    pull(controller) {
      if (offset >= bytes.length) {
        controller.close();
        return;
      }

      controller.enqueue(bytes.slice(offset, offset + size));
      offset += size;
    },
    cancel() {
      onCancel?.();
    },
  });
}

async function collect<T>(source: AsyncIterable<T>) {
  const items: T[] = [];
  for await (const item of source) {
    items.push(item);
  }

  return items;
}

function frame(event: ChatStreamEvent) {
  return `data: ${JSON.stringify(event)}\n\n`;
}

test('readSseData：事件跨块切分、汉字被切开时也能完整还原', async () => {
  const events: ChatStreamEvent[] = [
    { type: 'text_delta', text: '你好，' },
    { type: 'text_delta', text: '我来建一个任务。' },
    { type: 'done', messageId: 'm-1' },
  ];
  const body = events.map(frame).join('');

  for (const size of [1, 2, 3, 5, 7, 64]) {
    const data = await collect(readSseData(chunked(body, size)));
    assert.deepEqual(
      data.map((d) => JSON.parse(d) as ChatStreamEvent),
      events,
      `按 ${size} 字节切块`,
    );
  }
});

test('readSseData：流结束时没有空行收尾的最后一个事件也会产出', async () => {
  const data = await collect(
    readSseData(chunked('data: {"type":"text_delta","text":"a"}\n\ndata: {"type":"done"}', 4)),
  );

  assert.deepEqual(data, ['{"type":"text_delta","text":"a"}', '{"type":"done"}']);
});

test('readSseData：支持 CRLF 与单独 CR 行尾，多行 data 以换行拼接，忽略注释和其他字段', async () => {
  const body =
    ': keep-alive\r\n\r\nevent: message\r\nid: 1\r\ndata: 第一行\r\ndata:第二行\r\n\r\ndata: x\r\rdata: y\r\r';

  for (const size of [1, 3, 100]) {
    assert.deepEqual(
      await collect(readSseData(chunked(body, size))),
      ['第一行\n第二行', 'x', 'y'],
      `按 ${size} 字节切块`,
    );
  }
});

test('readSseData：提前结束遍历时取消底层的流', async () => {
  let cancelled = false;
  const stream = chunked(frame({ type: 'text_delta', text: 'a' }) + frame({ type: 'text_delta', text: 'b' }), 1, () => {
    cancelled = true;
  });

  for await (const data of readSseData(stream)) {
    assert.match(data, /"a"/);
    break;
  }

  assert.equal(cancelled, true);
});

function fakeApi(response: () => Response) {
  const requests: { url: string; init?: RequestInit }[] = [];
  const config: ApiConfig = {
    baseUrl: 'http://api.test/',
    getToken: () => 'token-1',
    fetch: (async (url: RequestInfo | URL, init?: RequestInit) => {
      requests.push({ url: String(url), init });
      return response();
    }) as typeof fetch,
  };
  return { config, requests };
}

test('streamChat：无法解析的事件被跳过，后续事件照常产出', async () => {
  const body =
    frame({ type: 'text_delta', text: '好的' }) +
    'data: {这不是 JSON\n\n' +
    'data: {"text":"缺少 type"}\n\n' +
    'data: 42\n\n' +
    frame({ type: 'tool_call', callId: 'c-1', actionKey: 'tasks.create', data: { title: '写周报' } }) +
    frame({ type: 'done' });
  const { config, requests } = fakeApi(
    () => new Response(chunked(body, 9), { headers: { 'Content-Type': 'text/event-stream' } }),
  );

  const events = await collect(streamChat(config, 'conv-1', '帮我建个任务'));

  assert.deepEqual(
    events.map((e) => e.type),
    ['text_delta', 'tool_call', 'done'],
  );
  assert.equal(requests[0].url, 'http://api.test/api/conversations/conv-1/messages');
  assert.equal(requests[0].init?.method, 'POST');
  assert.equal(new Headers(requests[0].init?.headers).get('Authorization'), 'Bearer token-1');
  assert.deepEqual(JSON.parse(String(requests[0].init?.body)), { text: '帮我建个任务' });
});

test('streamChat：请求失败时产出带后端提示的 error 事件', async () => {
  const problem = { title: '上一条消息还在回复中，请等它完成后再发送。', code: 'conversation.busy' };
  const { config } = fakeApi(() => Response.json(problem, { status: 409 }));

  assert.deepEqual(await collect(streamChat(config, 'conv-1', 'hi')), [{ type: 'error', text: problem.title }]);
});

test('streamChat：错误响应不是 JSON 时给出状态码', async () => {
  const { config } = fakeApi(() => new Response('<html>Bad Gateway</html>', { status: 502 }));

  assert.deepEqual(await collect(streamChat(config, 'conv-1', 'hi')), [{ type: 'error', text: '请求失败（502）' }]);
});
