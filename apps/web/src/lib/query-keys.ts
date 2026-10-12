/** 查询键集中定义，实时通道按实体失效对应的键。 */
export const queryKeys = {
  session: ['session'] as const,
  agents: ['agents'] as const,
  agent: (id: string) => ['agents', id] as const,
  orgChart: ['agents', 'org-chart'] as const,
  workItems: (filters?: object) => ['work-items', filters ?? {}] as const,
  workItem: (id: string) => ['work-item', id] as const,
  runs: (agentId?: string) => ['runs', agentId ?? 'all'] as const,
  run: (id: string) => ['run', id] as const,
  approvals: (status?: string) => ['approvals', status ?? 'all'] as const,
  conversations: ['conversations'] as const,
  messages: (id: string) => ['messages', id] as const,
  members: ['members'] as const,
  roles: ['roles'] as const,
  features: ['features'] as const,
  modules: ['modules'] as const,
  usage: ['usage'] as const,
  models: ['models'] as const,
  knowledgeDocuments: (search?: string) => ['knowledge', 'documents', search ?? ''] as const,
  knowledgeDocument: (id: string) => ['knowledge', 'document', id] as const,
  knowledgeSearch: (query: string) => ['knowledge', 'search', query] as const,
  knowledgeStatus: ['knowledge', 'status'] as const,
  memories: (filters?: object) => ['memories', filters ?? {}] as const,
  apiKeys: ['api-keys'] as const,
};

/** 实体变化 → 需要失效的查询前缀。 */
export const invalidationMap: Record<string, (id: string) => readonly (readonly unknown[])[]> = {
  agent: () => [['agents'], ['runs']],
  work_item: (id) => [['work-items'], ['work-item', id]],
  run: (id) => [['runs'], ['run', id], ['agents']],
  approval: () => [['approvals']],
  conversation: () => [['conversations']],
  module: () => [['modules'], ['session']],
  knowledge_document: () => [['knowledge']],
  memory: () => [['memories'], ['knowledge', 'search'], ['knowledge', 'status']],
};
