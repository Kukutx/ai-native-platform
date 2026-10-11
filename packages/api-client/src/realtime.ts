import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { RunEventDto } from './generated';
import type { ChatStreamEvent } from './stream';

/** 实体变化通知：客户端据此让对应查询失效重取。 */
export interface RealtimeChange {
  entity: 'agent' | 'work_item' | 'run' | 'approval' | 'conversation' | 'module';
  id: string;
  kind: 'created' | 'updated' | 'deleted';
}

export interface RealtimeOptions {
  baseUrl: string;
  getToken: () => string | null | undefined;
}

/**
 * 工作台实时通道（SignalR）：实体变化、运行过程逐字输出、流式对话。
 */
export class RealtimeClient {
  readonly connection: HubConnection;
  /** 已订阅的运行及引用计数；重连后服务端分组会丢失，需要重新加入。 */
  private readonly runs = new Map<string, number>();
  private starting: Promise<void> | undefined;

  constructor(options: RealtimeOptions) {
    this.connection = new HubConnectionBuilder()
      .withUrl(`${options.baseUrl.replace(/\/+$/, '')}/hubs/realtime`, {
        accessTokenFactory: () => options.getToken() ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.onreconnected(() => void this.resubscribe());
  }

  get connected() {
    return this.connection.state === HubConnectionState.Connected;
  }

  /** 可重复调用：并发调用共享同一次连接过程。 */
  async start() {
    if (this.connection.state === HubConnectionState.Disconnected) {
      this.starting ??= this.connection
        .start()
        .then(() => this.resubscribe())
        .finally(() => {
          this.starting = undefined;
        });
    }

    await this.starting;
  }

  stop() {
    return this.connection.stop();
  }

  onChange(handler: (change: RealtimeChange) => void) {
    this.connection.on('change', handler);
    return () => this.connection.off('change', handler);
  }

  onRunEvent(handler: (event: RunEventDto) => void) {
    this.connection.on('runEvent', handler);
    return () => this.connection.off('runEvent', handler);
  }

  /** 订阅某次运行的实时过程；连接未就绪时会在连上（或重连）后自动加入。 */
  async subscribeRun(runId: string) {
    this.runs.set(runId, (this.runs.get(runId) ?? 0) + 1);
    if (this.connected) {
      await this.connection.invoke('SubscribeRun', runId);
    }
  }

  async unsubscribeRun(runId: string) {
    const count = (this.runs.get(runId) ?? 0) - 1;
    if (count > 0) {
      this.runs.set(runId, count);
      return;
    }

    this.runs.delete(runId);
    if (this.connected) {
      await this.connection.invoke('UnsubscribeRun', runId);
    }
  }

  private async resubscribe() {
    await Promise.allSettled([...this.runs.keys()].map((id) => this.connection.invoke('SubscribeRun', id)));
  }

  /** 通过 WebSocket 流式对话（与 streamChat 的 SSE 版本等价）。 */
  streamChat(conversationId: string, text: string, onEvent: (event: ChatStreamEvent) => void) {
    return new Promise<void>((resolve, reject) => {
      this.connection.stream<ChatStreamEvent>('StreamChat', conversationId, text).subscribe({
        next: onEvent,
        complete: resolve,
        error: reject,
      });
    });
  }
}
