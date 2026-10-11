import type { ReactNode } from 'react';
import { ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View, type TextInputProps, type ViewStyle } from 'react-native';
import { usePalette } from '@/lib/theme';

export function Button({
  title,
  onPress,
  variant = 'primary',
  disabled,
  loading,
  style,
}: {
  title: string;
  onPress: () => void;
  variant?: 'primary' | 'outline' | 'danger';
  disabled?: boolean;
  loading?: boolean;
  style?: ViewStyle;
}) {
  const p = usePalette();
  const filled = variant === 'primary';
  const color = variant === 'danger' ? p.danger : filled ? p.primaryText : p.text;
  return (
    <Pressable
      accessibilityRole='button'
      onPress={onPress}
      disabled={disabled || loading}
      style={({ pressed }) => [
        styles.button,
        { backgroundColor: filled ? p.primary : 'transparent', borderColor: variant === 'danger' ? p.danger : filled ? p.primary : p.border },
        (pressed || disabled) && { opacity: 0.6 },
        style,
      ]}
    >
      {loading ? <ActivityIndicator color={color} /> : <Text style={[styles.buttonText, { color }]}>{title}</Text>}
    </Pressable>
  );
}

export function Field({ label, ...props }: TextInputProps & { label: string }) {
  const p = usePalette();
  return (
    <View style={styles.field}>
      <Text style={[styles.label, { color: p.text }]}>{label}</Text>
      <TextInput
        placeholderTextColor={p.muted}
        autoCapitalize='none'
        {...props}
        style={[styles.input, { color: p.text, borderColor: p.border, backgroundColor: p.card }, props.multiline && styles.multiline, props.style]}
      />
    </View>
  );
}

export function Card({ children, style }: { children: ReactNode; style?: ViewStyle }) {
  const p = usePalette();
  return <View style={[styles.card, { backgroundColor: p.card, borderColor: p.border }, style]}>{children}</View>;
}

export function Chip({ label, active, onPress }: { label: string; active?: boolean; onPress?: () => void }) {
  const p = usePalette();
  return (
    <Pressable
      onPress={onPress}
      style={[styles.chip, { borderColor: active ? p.primary : p.border, backgroundColor: active ? p.primary : 'transparent' }]}
    >
      <Text style={{ color: active ? p.primaryText : p.text, fontSize: 13 }}>{label}</Text>
    </Pressable>
  );
}

export function Muted({ children, style }: { children: ReactNode; style?: object }) {
  const p = usePalette();
  return <Text style={[{ color: p.muted, fontSize: 13 }, style]}>{children}</Text>;
}

export function Empty({ title, hint }: { title: string; hint?: string }) {
  const p = usePalette();
  return (
    <View style={styles.empty}>
      <Text style={{ color: p.text, fontSize: 16, fontWeight: '600' }}>{title}</Text>
      {hint ? <Muted style={{ marginTop: 6, textAlign: 'center' }}>{hint}</Muted> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  button: { height: 46, borderRadius: 10, borderWidth: 1, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 16 },
  buttonText: { fontSize: 15, fontWeight: '600' },
  field: { gap: 6 },
  label: { fontSize: 14, fontWeight: '500' },
  input: { borderWidth: 1, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 10, fontSize: 15 },
  multiline: { minHeight: 96, textAlignVertical: 'top' },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  chip: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 12, paddingVertical: 6 },
  empty: { alignItems: 'center', paddingVertical: 64, paddingHorizontal: 24 },
});
