import { Badge } from '@/components/ui/badge';

export function StatusBadge({ meta }: { meta: { label: string; tone: 'default' | 'secondary' | 'destructive' | 'outline' } }) {
  return <Badge variant={meta.tone}>{meta.label}</Badge>;
}
