import { Badge } from '@/components/ui/badge';
import type { Tone } from '@/lib/labels';

export function StatusBadge({ meta }: { meta: { label: string; tone: Tone } }) {
  return <Badge variant={meta.tone}>{meta.label}</Badge>;
}
