import { View } from 'react-native';
import { usePalette } from '@/lib/theme';

/** 细进度条，percent 取 0–100。 */
export function ProgressBar({ percent }: { percent: number }) {
  const p = usePalette();
  return (
    <View style={{ height: 6, borderRadius: 3, backgroundColor: p.border, overflow: 'hidden' }}>
      <View style={{ width: `${Math.max(0, Math.min(100, percent))}%`, height: '100%', borderRadius: 3, backgroundColor: p.primary }} />
    </View>
  );
}
