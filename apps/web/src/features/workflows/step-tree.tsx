import type { ActionDescriptorDto, AgentDto, WorkflowStep } from '@ainative/api-client';
import { Badge } from '@/components/ui/badge';
import { workflowStepKind } from '@/lib/labels';
import { describeStep } from './describe';

/** 工作流定义的只读展示，分支和遍历里的步骤缩进列在下面。 */
export function StepTree({ steps, actions, agents }: { steps: WorkflowStep[]; actions?: ActionDescriptorDto[]; agents?: AgentDto[] }) {
  return (
    <ol className='grid gap-3'>
      {steps.map((step, index) => (
        <li key={step.key} className='flex gap-3'>
          <Badge variant='outline' className='h-6 font-mono'>
            {index + 1}
          </Badge>
          <div className='min-w-0 flex-1 space-y-2'>
            <div>
              <p className='font-medium'>
                {step.name}
                <span className='text-muted-foreground ml-2 text-xs font-normal'>{workflowStepKind[step.kind ?? 'action'].label}</span>
              </p>
              <p className='text-muted-foreground line-clamp-2 text-sm break-all'>{describeStep(step, actions, agents)}</p>
              {step.when ? <p className='text-muted-foreground font-mono text-xs'>条件：{step.when}</p> : null}
            </div>
            {step.kind === 'branch'
              ? step.branches?.map((branch, b) => (
                  <div key={b} className='space-y-2 border-l-2 pl-3'>
                    <p className='text-sm'>
                      {branch.name}
                      <span className='text-muted-foreground ml-2 font-mono text-xs'>{branch.when ? branch.when : '其他情况'}</span>
                    </p>
                    {branch.steps?.length ? <StepTree steps={branch.steps} actions={actions} agents={agents} /> : <p className='text-muted-foreground text-xs'>不做任何事</p>}
                  </div>
                ))
              : null}
            {step.kind === 'for_each' && step.steps?.length ? (
              <div className='border-l-2 pl-3'>
                <StepTree steps={step.steps} actions={actions} agents={agents} />
              </div>
            ) : null}
          </div>
        </li>
      ))}
    </ol>
  );
}
