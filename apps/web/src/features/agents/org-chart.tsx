import Link from 'next/link';
import type { OrgChartNodeDto } from '@ainative/api-client';
import { StatusBadge } from '@/components/app/status-badge';
import { agentStatus } from '@/lib/labels';

/** 组织架构树：谁向谁汇报。 */
export function OrgChart({ nodes }: { nodes: OrgChartNodeDto[] }) {
  return (
    <ul className='space-y-2'>
      {nodes.map((node) => (
        <OrgNode key={node.agent.id} node={node} />
      ))}
    </ul>
  );
}

function OrgNode({ node }: { node: OrgChartNodeDto }) {
  return (
    <li>
      <Link
        href={`/agents/view?id=${node.agent.id}`}
        className='hover:bg-accent inline-flex items-center gap-3 rounded-lg border px-3 py-2 text-sm transition-colors'
      >
        <span className='bg-muted flex size-8 items-center justify-center rounded-full font-medium'>{node.agent.name.slice(0, 1)}</span>
        <span>
          <span className='font-medium'>{node.agent.name}</span>
          <span className='text-muted-foreground ml-2'>{node.agent.title}</span>
        </span>
        <StatusBadge meta={agentStatus[node.agent.status]} />
      </Link>
      {node.reports.length ? (
        <ul className='mt-2 ml-6 space-y-2 border-l pl-4'>
          {node.reports.map((child) => (
            <OrgNode key={child.agent.id} node={child} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}
