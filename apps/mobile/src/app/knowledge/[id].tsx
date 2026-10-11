import { ActivityIndicator, ScrollView, StyleSheet, Text } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { usePalette } from '@/lib/theme';

export default function KnowledgeDocumentScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const detail = useQuery({ queryKey: ['knowledge', 'document', id], queryFn: ({ signal }) => getApi().knowledge.getDocument(id, signal) });

  if (!detail.data) {
    return <ActivityIndicator style={{ marginTop: 48 }} />;
  }

  const { document, content } = detail.data;
  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container}>
      <Stack.Screen options={{ title: document.title }} />
      <Text style={{ color: p.text, fontSize: 20, fontWeight: '600' }}>{document.title}</Text>
      <Muted>
        {document.tags.length ? `${document.tags.join('、')} · ` : ''}
        {document.createdBy.name} · {new Date(document.updatedAt).toLocaleDateString('zh-CN')}
      </Muted>
      <Text selectable style={{ color: p.text, fontSize: 15, lineHeight: 24 }}>
        {content}
      </Text>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { padding: 16, gap: 10 },
});
