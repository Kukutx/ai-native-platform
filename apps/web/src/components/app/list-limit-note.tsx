/**
 * 详情页的关联列表只带前一部分（后端上限 200 条），总数更多时提示用户去列表页筛选查看全部。
 */
export function ListLimitNote({ shown, total }: { shown: number; total: number }) {
  if (total <= shown) {
    return null;
  }

  return (
    <p className='text-muted-foreground pt-2 text-xs'>
      只显示前 {shown} 个，共 {total} 个。
    </p>
  );
}
