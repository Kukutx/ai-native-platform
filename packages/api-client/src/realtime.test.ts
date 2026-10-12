import assert from 'node:assert/strict';
import { afterEach, beforeEach, mock, test } from 'node:test';
import { HubConnectionState } from '@microsoft/signalr';
import { RealtimeClient } from './realtime';

/** 首次连接失败后的第一次重试间隔（与 realtime.ts 的 RETRY_DELAYS[0] 一致）。 */
const FIRST_RETRY_MS = 2_000;

/**
 * 不连真实服务：把 SignalR 连接的 start/stop/state 换成按脚本返回的替身，
 * 只验证 RealtimeClient 自己的状态机（重试、重取通知、启停顺序）。
 */
function fakeRealtime(outcomes: ('ok' | 'fail')[]) {
  const client = new RealtimeClient({ baseUrl: 'http://realtime.test', getToken: () => 'token-1' });
  let state = HubConnectionState.Disconnected;
  const fake = { starts: 0, stops: 0, resyncs: 0 };
  Object.defineProperty(client.connection, 'state', { get: () => state, configurable: true });
  Object.assign(client.connection, {
    start: async () => {
      const outcome = outcomes[fake.starts++] ?? 'fail';
      if (outcome === 'fail') {
        throw new Error('连接失败');
      }

      state = HubConnectionState.Connected;
    },
    stop: async () => {
      fake.stops++;
      state = HubConnectionState.Disconnected;
    },
    invoke: async () => undefined,
  });
  client.onResync(() => fake.resyncs++);
  return { client, fake };
}

/** 等排队中的启停步骤（全是微任务）跑完。 */
const settle = () => new Promise<void>((resolve) => setImmediate(resolve));

beforeEach(() => mock.timers.enable({ apis: ['setTimeout'] }));
afterEach(() => mock.timers.reset());

test('RealtimeClient：第一次就连上时不通知重取', async () => {
  const { client, fake } = fakeRealtime(['ok']);

  await client.start();

  assert.equal(client.connected, true);
  assert.equal(fake.resyncs, 0);
});

test('RealtimeClient：首次连接失败、之后重试连上时通知重取，补上连不上期间错过的变化', async () => {
  const { client, fake } = fakeRealtime(['fail', 'ok']);

  await assert.rejects(client.start(), /连接失败/);
  assert.equal(fake.resyncs, 0);

  mock.timers.tick(FIRST_RETRY_MS);
  await settle();

  assert.equal(fake.starts, 2);
  assert.equal(client.connected, true);
  assert.equal(fake.resyncs, 1);
});

test('RealtimeClient：停止后不再执行排队中的重试', async () => {
  const { client, fake } = fakeRealtime(['fail', 'ok']);

  await assert.rejects(client.start());
  await client.stop();
  mock.timers.tick(60_000);
  await settle();

  assert.equal(fake.starts, 1);
  assert.equal(client.connected, false);
});

test('RealtimeClient：断开后再次启动视为重连，通知重取', async () => {
  const { client, fake } = fakeRealtime(['ok', 'ok']);

  await client.start();
  await client.stop();
  await client.start();

  assert.equal(fake.stops, 1);
  assert.equal(fake.starts, 2);
  assert.equal(fake.resyncs, 1);
});

test('RealtimeClient：重复调用 start 只建立一次连接', async () => {
  const { client, fake } = fakeRealtime(['ok', 'ok']);

  await Promise.all([client.start(), client.start()]);

  assert.equal(fake.starts, 1);
  assert.equal(fake.resyncs, 0);
});
