import {
  ActionsClient,
  AgentsClient,
  ApiException,
  ApiKeysClient,
  ApprovalsClient,
  AuthClient,
  ConversationsClient,
  KnowledgeClient,
  MembersClient,
  MemoriesClient,
  ModelsClient,
  ModulesClient,
  OrganizationsClient,
  RolesClient,
  RunsClient,
  UsageClient,
  WorkItemsClient,
  WorkflowsClient,
} from './generated';

export interface ApiConfig {
  /** API 根地址，例如 http://localhost:5080 */
  baseUrl: string;
  /** 当前访问令牌；未登录返回 null。 */
  getToken: () => string | null | undefined;
  /** 令牌失效（401）时回调，客户端据此回到登录页。 */
  onUnauthorized?: () => void;
  /** 自定义 fetch（例如 Expo 的 expo/fetch 支持流式读取）。 */
  fetch?: typeof fetch;
}

export type HttpLike = { fetch(url: RequestInfo, init?: RequestInit): Promise<Response> };

export function createHttp(config: ApiConfig): HttpLike {
  const doFetch = config.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => globalThis.fetch(input, init));
  return {
    async fetch(url, init) {
      const headers = new Headers(init?.headers);
      const token = config.getToken();
      if (token) {
        headers.set('Authorization', `Bearer ${token}`);
      }

      const response = await doFetch(url, { ...init, headers });
      if (response.status === 401 && token) {
        config.onUnauthorized?.();
      }

      return response;
    },
  };
}

/**
 * 按资源分组的类型安全客户端，全部由后端 OpenAPI 生成。
 */
export function createApi(config: ApiConfig) {
  const http = createHttp(config);
  const baseUrl = config.baseUrl.replace(/\/+$/, '');
  return {
    auth: new AuthClient(baseUrl, http),
    agents: new AgentsClient(baseUrl, http),
    workItems: new WorkItemsClient(baseUrl, http),
    runs: new RunsClient(baseUrl, http),
    approvals: new ApprovalsClient(baseUrl, http),
    actions: new ActionsClient(baseUrl, http),
    conversations: new ConversationsClient(baseUrl, http),
    knowledge: new KnowledgeClient(baseUrl, http),
    memories: new MemoriesClient(baseUrl, http),
    apiKeys: new ApiKeysClient(baseUrl, http),
    members: new MembersClient(baseUrl, http),
    roles: new RolesClient(baseUrl, http),
    organizations: new OrganizationsClient(baseUrl, http),
    modules: new ModulesClient(baseUrl, http),
    usage: new UsageClient(baseUrl, http),
    models: new ModelsClient(baseUrl, http),
    workflows: new WorkflowsClient(baseUrl, http),
  };
}

export type Api = ReturnType<typeof createApi>;

export interface Problem {
  status: number;
  title: string;
  code: string;
  errors?: Record<string, string[]>;
}

/**
 * 把接口错误统一解析成 ProblemDetails（title 为中文提示，code 供程序判断）。
 */
export function toProblem(error: unknown): Problem {
  if (ApiException.isApiException(error)) {
    try {
      const body = JSON.parse(error.response) as Partial<Problem>;
      return {
        status: error.status,
        title: body.title ?? error.message,
        code: body.code ?? 'unknown',
        errors: body.errors,
      };
    } catch {
      return { status: error.status, title: error.message, code: 'unknown' };
    }
  }

  return { status: 0, title: error instanceof Error ? error.message : '网络异常，请稍后重试。', code: 'network_error' };
}
