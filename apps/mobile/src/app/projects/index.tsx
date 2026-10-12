import { useDeferredValue, useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, TextInput, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { ProgressBar } from '@/components/progress';
import { Chip, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { formatDate, formatProgress, projectStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ProjectsScreen() {
  const p = usePalette();
  const [openOnly, setOpenOnly] = useState(true);
  const [search, setSearch] = useState('');
  const term = useDeferredValue(search.trim()) || undefined;
  const projects = useQuery({
    queryKey: ['projects', { search: term, openOnly }],
    queryFn: ({ signal }) => getApi().projects.list(term, undefined, openOnly, 1, 100, signal),
    placeholderData: keepPreviousData,
  });

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '项目' }} />
      <FlatList
        data={projects.data?.items ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        keyboardShouldPersistTaps='handled'
        refreshControl={<RefreshControl refreshing={projects.isRefetching} onRefresh={() => void projects.refetch()} />}
        ListHeaderComponent={
          <View style={styles.header}>
            <TextInput
              value={search}
              onChangeText={setSearch}
              placeholder='搜索项目'
              placeholderTextColor={p.muted}
              style={[styles.search, { color: p.text, borderColor: p.border, backgroundColor: p.card }]}
              clearButtonMode='while-editing'
            />
            <View style={styles.chips}>
              <Chip label='未结束' active={openOnly} onPress={() => setOpenOnly(true)} />
              <Chip label='全部' active={!openOnly} onPress={() => setOpenOnly(false)} />
            </View>
          </View>
        }
        ListEmptyComponent={
          projects.isPending ? null : <Empty title={term ? '没有找到' : '还没有项目'} hint={term ? '换个关键字试试。' : '在网页端新建，或者在对话里让智能体帮你规划一个项目。'} />
        }
        renderItem={({ item }) => (
          <Pressable
            onPress={() => router.push({ pathname: '/projects/[id]', params: { id: item.id } })}
            style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}
          >
            <View style={styles.cardTop}>
              <Text style={{ color: p.text, fontSize: 15, fontWeight: '500', flex: 1 }} numberOfLines={2}>
                {item.name}
              </Text>
              <Text style={{ color: item.isOverdue ? p.warning : p.muted, fontSize: 13 }}>{item.isOverdue ? '已逾期' : projectStatus[item.status]}</Text>
            </View>
            <ProgressBar percent={item.progress.percent} />
            <Muted>{formatProgress(item.progress)}</Muted>
            <Muted>
              {[item.lead ? `负责人 ${item.lead.name ?? '—'}${item.lead.kind === 'agent' ? '（智能体）' : ''}` : '', item.targetDate ? `目标 ${formatDate(item.targetDate)}` : '']
                .filter(Boolean)
                .join(' · ') || '未设负责人'}
            </Muted>
          </Pressable>
        )}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 10 },
  header: { gap: 10, marginBottom: 4 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  search: { borderWidth: 1, borderRadius: 10, paddingHorizontal: 12, paddingVertical: 9, fontSize: 15 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  cardTop: { flexDirection: 'row', justifyContent: 'space-between', gap: 12 },
});
