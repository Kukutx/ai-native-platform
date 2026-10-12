import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import type { RunEventDto } from './generated';
import type { ChatStreamEvent } from './stream';

/**
 * 实体变化通知：客户端据此让对应查询失效重取。
 * entity 与后端各模块的实时实体常量一一对应（RealtimeEntities、CrmRealtime、ErpRealtime、ProjectRealtime、
 * AnalyticsRealtime、IntegrationRealtime、TenantRealtime），后端新增实体时在这里补上。
 */
export interface RealtimeChange {
  entity:
    | 'agent'
    | 'work_item'
    | 'run'
    | 'approval'
    | 'conversation'
    | 'module'
    | 'knowledge_document'
    | 'memory'
    | 'workflow'
    | 'workflow_run'
    | 'crm_company'
    | 'crm_contact'
    | 'crm_deal'
    | 'crm_activity'
    | 'crm_stage'
    | 'crm_field'
    | 'erp_product'
    | 'erp_warehouse'
    | 'erp_stock'
    | 'erp_sales_order'
    | 'erp_purchase_order'
    | 'erp_stock_document'
    | 'project'
    | 'analytics_report'
    | 'mcp_server'
    | 'tenant_secret'
    | 'tenant';
  id: string;
  kind: 'created' | 'updated' | 'deleted';
}

/** 首次连接失败、或自动重连放弃后，按这个节奏继续重试（毫秒）。 */
const RETRY_DELAYS = [2_000, 5_000, 10_000, 30_000];

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
  private readonly resyncHandlers = new Set<() => void>();
  /** start/stop 串行执行：上一个切换（例如切换空间时的断开）完成后才开始下一个。 */
  private lifecycle: Promise<void> = Promise.resolve();
  /** 最后一次 start/stop 调用表达的意图，决定排队中的切换要不要执行。 */
  private wanted = false;
  private hasConnected = false;
  /** 有过连接失败：这期间的变化没有推送过来，之后第一次连上（哪怕是第一次连上）也要通知重取。 */
  private missedChanges = false;
  private retryAttempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;

  constructor(options: RealtimeOptions) {
    this.connection = new HubConnectionBuilder()
      // 跳过协商直接建立 WebSocket：整个连接只有一条长连接，服务端多实例部署时负载均衡不需要会话保持。
      .withUrl(`${options.baseUrl.replace(/\/+$/, '')}/hubs/realtime`, {
        accessTokenFactory: () => options.getToken() ?? '',
        skipNegotiation: true,
        transport: HttpTransportType.WebSockets,
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
    this.connection.onreconnected(() => void this.afterConnected());
    this.connection.onclose(() => {
      // 主动 stop 时 wanted 为 false；否则是自动重连也放弃了，继续按退避节奏重试。
      if (this.wanted) {
        this.scheduleRetry();
      }
    });
  }

  get connected() {
    return this.connection.state === HubConnectionState.Connected;
  }

  /** 可重复调用；与 stop 按调用顺序串行执行，最后一次调用决定最终状态。连接失败会在后台继续重试。 */
  start() {
    this.wanted = true;
    return this.enqueue(async () => {
      if (!this.wanted || this.connection.state !== HubConnectionState.Disconnected) {
        return;
      }

      clearTimeout(this.retryTimer);
      try {
        await this.connection.start();
      } catch (error) {
        this.missedChanges = true;
        this.scheduleRetry();
        throw error;
      }

      await this.afterConnected();
    });
  }

  stop() {
    this.wanted = false;
    clearTimeout(this.retryTimer);
    return this.enqueue(async () => {
      if (!this.wanted) {
        await this.connection.stop();
      }
    });
  }

  /**
   * 断线期间错过的变化不会补发：重新连上时调用，客户端应让当前查询全部重取。
   * 第一次就连上时不调用；第一次连接失败、之后重试连上时同样会调用，补上连不上期间错过的变化。
   */
  onResync(handler: () => void) {
    this.resyncHandlers.add(handler);
    return () => void this.resyncHandlers.delete(handler);
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

  private async afterConnected() {
    this.retryAttempt = 0;
    if (this.hasConnected || this.missedChanges) {
      this.resyncHandlers.forEach((handler) => handler());
    }

    this.hasConnected = true;
    this.missedChanges = false;
    // 重连后服务端分组已丢失，重新加入正在查看的运行。
    await Promise.allSettled([...this.runs.keys()].map((id) => this.connection.invoke('SubscribeRun', id)));
  }

  private scheduleRetry() {
    clearTimeout(this.retryTimer);
    const delay = RETRY_DELAYS[Math.min(this.retryAttempt++, RETRY_DELAYS.length - 1)];
    this.retryTimer = setTimeout(() => {
      if (this.wanted) {
        this.start().catch(() => {
          // 失败会再次排期重试。
        });
      }
    }, delay);
  }

  private enqueue(step: () => Promise<void>) {
    const run = this.lifecycle.then(step);
    this.lifecycle = run.catch(() => undefined);
    return run;
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
