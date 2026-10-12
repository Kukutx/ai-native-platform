import type { RoleDto } from '@ainative/api-client';

export function toggle(list: string[], value: string, checked: boolean) {
  return checked ? [...new Set([...list, value])] : list.filter((v) => v !== value);
}

export function RoleChecklist({ roles, value, onChange }: { roles: RoleDto[]; value: string[]; onChange: (value: string[]) => void }) {
  return (
    <div className='grid gap-2 sm:grid-cols-2'>
      {roles.map((role) => (
        <label key={role.id} className='flex items-start gap-2 rounded-md border p-2 text-sm'>
          <input
            type='checkbox'
            className='accent-primary mt-0.5'
            checked={value.includes(role.id)}
            onChange={(e) => onChange(toggle(value, role.id, e.target.checked))}
          />
          <span>
            <span className='font-medium'>{role.name}</span>
            {role.description ? <span className='text-muted-foreground block text-xs'>{role.description}</span> : null}
          </span>
        </label>
      ))}
    </div>
  );
}
