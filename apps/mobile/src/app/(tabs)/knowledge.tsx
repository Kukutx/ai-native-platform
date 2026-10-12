import { useDeferredValue, useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, TextInput, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import type { KnowledgeHitDto } from '@ainative/api-client';
import { TabGuard } from '@/components/tab-guard';
import { Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { usePalette } from '@/lib/theme';

function openDocument(id: string) {
  router.push({ pathname: '/knowledge/[id]', params: { id } });
}

export default function KnowledgeTab() {
  return (
    <TabGuard tab='knowledge'>
      <KnowledgeScreen />
    </TabGuard>
  );
}

function KnowledgeScreen() {
  const p = usePalette();
  const [search, setSearch] = useState('');
  const query = useDeferredValue(search.trim());
  const documents = useQuery({
    queryKey: ['knowledge', 'documents', ''],
    queryFn: ({ signal }) => getApi().knowledge.listDocuments(undefined, signal),
    enabled: !query,
  });
  const results = useQuery({
    queryKey: ['knowledge', 'search', query],
    queryFn: ({ signal }) => getApi().knowledge.search(query, true, true, 20, undefined, signal),
    enabled: !!query,
  });

  const header = (
    <TextInput
      value={search}
      onChangeText={setSearch}
      placeholder='检索文档和记忆，例如：住宿标准'
      placeholderTextColor={p.muted}
      returnKeyType='search'
      clearButtonMode='while-editing'
      style={[styles.search, { color: p.text, borderColor: p.border, backgroundColor: p.card }]}
    />
  );

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '知识' }} />
      {query ? (
        <FlatList<KnowledgeHitDto>
          data={results.data ?? []}
          keyExtractor={(hit) => `${hit.sourceId}-${hit.seq}`}
          contentContainerStyle={styles.list}
          keyboardShouldPersistTaps='handled'
          ListHeaderComponent={header}
          ListEmptyComponent={results.isPending ? null : <Empty title='没有找到相关内容' hint='换个说法试试。' />}
          renderItem={({ item }) => (
            <Pressable
              disabled={item.sourceKind !== 'document'}
              onPress={() => openDocument(item.sourceId)}
              style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}
            >
              <View style={styles.row}>
                <Text style={{ color: p.text, fontWeight: '600', flexShrink: 1 }} numberOfLines={1}>
                  {item.sourceKind === 'document' ? `《${item.title}》` : item.title}
                </Text>
                <Muted>{[item.semanticMatch ? '语义' : null, item.keywordMatch ? '关键词' : null].filter(Boolean).join('+')}</Muted>
              </View>
              <Text style={{ color: p.text, fontSize: 14, lineHeight: 20 }} numberOfLines={4}>
                {item.content}
              </Text>
            </Pressable>
          )}
        />
      ) : (
        <FlatList
          data={documents.data ?? []}
          keyExtractor={(d) => d.id}
          contentContainerStyle={styles.list}
          keyboardShouldPersistTaps='handled'
          refreshControl={<RefreshControl refreshing={documents.isRefetching} onRefresh={() => void documents.refetch()} />}
          ListHeaderComponent={header}
          ListEmptyComponent={documents.isPending ? null : <Empty title='知识库还是空的' hint='在 Web 或桌面端添加文档后，这里可以随时检索。' />}
          renderItem={({ item }) => (
            <Pressable onPress={() => openDocument(item.id)} style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}>
              <Text style={{ color: p.text, fontSize: 15, fontWeight: '500' }} numberOfLines={2}>
                {item.title}
              </Text>
              <Muted>
                {item.tags.length ? `${item.tags.join('、')} · ` : ''}
                {item.createdBy.name} · {item.chunkCount} 个片段
              </Muted>
            </Pressable>
          )}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 10 },
  search: { borderWidth: 1, borderRadius: 12, paddingHorizontal: 14, paddingVertical: 11, fontSize: 15, marginBottom: 4 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  row: { flexDirection: 'row', justifyContent: 'space-between', gap: 8 },
});
