'use client';

import { useMemo } from 'react';
import { useMutation, useQuery, useQueryClient, type QueryKey } from '@tanstack/react-query';
import type { PagedResultOfWorkItemDto, UpdateWorkItemRequest, WorkItemDto, WorkItemStatus } from '@ainative/api-client';
import { useSession } from '@/components/app/session-provider';
import { api } from '@/lib/api';
import { queryKeys } from '@/lib/query-keys';

/**
 * 修改任务的请求：接口是整体更新，没改的字段沿用 item 里的值。
 * expectedUpdatedAt 默认取 item 的版本，期间被别人或智能体改过时后端返回 409，而不是用旧值覆盖。
 */
export function updateRequest(item: WorkItemDto, patch: Partial<UpdateWorkItemRequest> = {}): UpdateWorkItemRequest {
  return {
    title: item.title,
    description: item.description,
    status: item.status,
    priority: item.priority,
    assigneeAgentId: item.assigneeAgentId,
    assigneeUserId: item.assigneeUserId,
    projectId: item.projectId,
    milestoneId: item.milestoneId,
    startDate: item.startDate,
    dueDate: item.dueDate,
    expectedUpdatedAt: item.updatedAt,
    ...patch,
  };
}

/** 已关闭（完成或取消）的任务。 */
export function isClosed(status: WorkItemStatus) {
  return status === 'done' || status === 'cancelled';
}

/**
 * 在看板或列表里改任务状态。传入列表的查询键时先在本地改好（乐观更新），失败（例如期间被别人改过）再改回来，
 * 失败原因由全局提示说明。
 */
export function useChangeTaskStatus(listKey?: QueryKey) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ task, status }: { task: WorkItemDto; status: WorkItemStatus }) => api.workItems.update(task.id, updateRequest(task, { status })),
    onMutate: async ({ task, status }) => {
      if (!listKey) {
        return undefined;
      }

      await queryClient.cancelQueries({ queryKey: listKey });
      const previous = queryClient.getQueryData<PagedResultOfWorkItemDto>(listKey);
      if (previous) {
        queryClient.setQueryData<PagedResultOfWorkItemDto>(listKey, {
          ...previous,
          items: previous.items.map((item) => (item.id === task.id ? { ...item, status } : item)),
        });
      }

      return { previous };
    },
    onError: (_error, _request, context) => {
      if (listKey && context?.previous) {
        queryClient.setQueryData(listKey, context.previous);
      }
    },
    onSettled: async (_data, _error, { task }) => {
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      await queryClient.invalidateQueries({ queryKey: queryKeys.workItem(task.id) });
    },
  });
}

/**
 * 任务负责人的显示名称：智能体名称或成员姓名。看不到成员或智能体名单（没有对应权限）时显示「成员」「智能体」。
 */
export function useAssigneeNames() {
  const { can } = useSession();
  const members = useQuery({ queryKey: queryKeys.members, queryFn: ({ signal }) => api.members.list(signal), enabled: can('platform.members.view') });
  const agents = useQuery({ queryKey: queryKeys.agents, queryFn: ({ signal }) => api.agents.list(signal), enabled: can('agents.view') });

  return useMemo(() => {
    const people = new Map(members.data?.map((m) => [m.userId, m.displayName]));
    const bots = new Map(agents.data?.map((a) => [a.id, a.name]));
    return {
      members: members.data,
      agents: agents.data,
      name: (task: Pick<WorkItemDto, 'assigneeAgentId' | 'assigneeUserId'>) => {
        if (task.assigneeAgentId) {
          return bots.get(task.assigneeAgentId) ?? '智能体';
        }

        if (task.assigneeUserId) {
          return people.get(task.assigneeUserId) ?? '成员';
        }

        return undefined;
      },
    };
  }, [members.data, agents.data]);
}
