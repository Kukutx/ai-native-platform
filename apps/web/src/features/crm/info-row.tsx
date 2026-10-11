/** 资料卡里的一行：图标、名称、内容；没有内容时显示破折号。 */
export function InfoRow({ icon: Icon, label, children }: { icon: React.ComponentType<{ className?: string }>; label: string; children: React.ReactNode }) {
  return (
    <div className='flex items-start gap-3'>
      <Icon className='text-muted-foreground mt-0.5 size-4 shrink-0' />
      <span className='text-muted-foreground w-14 shrink-0'>{label}</span>
      <span className='min-w-0 flex-1'>{children ?? <span className='text-muted-foreground'>—</span>}</span>
    </div>
  );
}
