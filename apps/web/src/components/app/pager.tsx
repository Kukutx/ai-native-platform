import { Button } from '@/components/ui/button';

export function Pager({ page, pageSize, total, onChange }: { page: number; pageSize: number; total: number; onChange: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (pages <= 1) {
    return null;
  }

  return (
    <div className='text-muted-foreground flex items-center justify-end gap-2 pt-4 text-sm'>
      <span>
        第 {page} / {pages} 页，共 {total} 条
      </span>
      <Button size='sm' variant='outline' disabled={page <= 1} onClick={() => onChange(page - 1)}>
        上一页
      </Button>
      <Button size='sm' variant='outline' disabled={page >= pages} onClick={() => onChange(page + 1)}>
        下一页
      </Button>
    </div>
  );
}
