import { useDeferredValue, useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, TextInput, View } from 'react-native';
import Ionicons from '@expo/vector-icons/Ionicons';
import { router, Stack } from 'expo-router';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { sessionCan, type CrmDealStatus } from '@ainative/api-client';
import { TabGuard } from '@/components/tab-guard';
import { Card, Chip, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { crmDealStatus, formatAmount, formatDate, formatTime, formatTotals } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

type Segment = 'deals' | 'companies' | 'contacts';

interface Row {
  id: string;
  title: string;
  lines: string[];
  right?: string;
  href: Parameters<typeof router.push>[0];
}

const segments: { key: Segment; label: string }[] = [
  { key: 'deals', label: '商机' },
  { key: 'companies', label: '客户' },
  { key: 'contacts', label: '联系人' },
];

export default function CrmTab() {
  return (
    <TabGuard tab='crm'>
      <CrmScreen />
    </TabGuard>
  );
}

function CrmScreen() {
  const p = usePalette();
  const [segment, setSegment] = useState<Segment>('deals');
  const [status, setStatus] = useState<CrmDealStatus>('open');
  const [search, setSearch] = useState('');
  const term = useDeferredValue(search.trim()) || undefined;
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const pipeline = useQuery({
    queryKey: ['crm', 'pipeline', 'all'],
    queryFn: ({ signal }) => getApi().crm.deals.pipeline(undefined, signal),
    enabled: segment === 'deals',
  });
  const rows = useQuery({
    queryKey: ['crm', segment, { search: term, status: segment === 'deals' ? status : undefined }],
    queryFn: async ({ signal }): Promise<Row[]> => {
      const api = getApi().crm;
      if (segment === 'deals') {
        const page = await api.deals.list(term, undefined, status, undefined, undefined, undefined, 1, 100, signal);
        return page.items.map((d) => ({
          id: d.id,
          title: d.title,
          lines: [[d.companyName, d.stageName].filter(Boolean).join(' · '), d.expectedCloseDate ? `预计 ${formatDate(d.expectedCloseDate)} 成交` : ''].filter(Boolean),
          right: formatAmount(d.amount, d.currency),
          href: { pathname: '/crm/deals/[id]', params: { id: d.id } },
        }));
      }

      if (segment === 'companies') {
        const page = await api.companies.list(term, undefined, undefined, 1, 100, signal);
        return page.items.map((c) => ({
          id: c.id,
          title: c.name,
          lines: [[c.industry, `${c.contactCount} 位联系人`, `${c.openDealCount} 个商机`].filter(Boolean).join(' · '), c.lastActivityAt ? `最近往来 ${formatTime(c.lastActivityAt)}` : ''].filter(Boolean),
          href: { pathname: '/crm/companies/[id]', params: { id: c.id } },
        }));
      }

      const page = await api.contacts.list(term, undefined, undefined, undefined, 1, 100, signal);
      return page.items.map((c) => ({
        id: c.id,
        title: c.name,
        lines: [[c.companyName, c.title].filter(Boolean).join(' · '), c.phone || c.email].filter(Boolean),
        href: { pathname: '/crm/contacts/[id]', params: { id: c.id } },
      }));
    },
    placeholderData: keepPreviousData,
  });
  const manage = sessionCan(session.data, 'crm.manage');

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen
        options={{
          title: '客户',
          headerRight: manage
            ? () => (
                <Pressable
                  onPress={() => router.push({ pathname: '/crm/new', params: { type: segment === 'deals' ? 'deal' : segment === 'companies' ? 'company' : 'contact' } })}
                  accessibilityLabel='新建'
                  style={styles.headerButton}
                >
                  <Ionicons name='add' size={26} color={p.text} />
                </Pressable>
              )
            : undefined,
        }}
      />
      <FlatList
        data={rows.data ?? []}
        keyExtractor={(item) => item.id}
        contentContainerStyle={styles.list}
        keyboardShouldPersistTaps='handled'
        refreshControl={
          <RefreshControl
            refreshing={rows.isRefetching}
            onRefresh={() => {
              void rows.refetch();
              void pipeline.refetch();
            }}
          />
        }
        ListHeaderComponent={
          <View style={styles.header}>
            <View style={styles.chips}>
              {segments.map((s) => (
                <Chip key={s.key} label={s.label} active={segment === s.key} onPress={() => setSegment(s.key)} />
              ))}
            </View>
            <TextInput
              value={search}
              onChangeText={setSearch}
              placeholder={segment === 'deals' ? '搜索商机' : segment === 'companies' ? '搜索客户名称、行业、电话' : '搜索姓名、邮箱、电话'}
              placeholderTextColor={p.muted}
              style={[styles.search, { color: p.text, borderColor: p.border, backgroundColor: p.card }]}
              clearButtonMode='while-editing'
            />
            {segment === 'deals' ? (
              <>
                {pipeline.data ? (
                  <Card>
                    <View style={styles.summary}>
                      <View style={styles.summaryItem}>
                        <Muted>进行中</Muted>
                        <Text style={[styles.amount, { color: p.text }]}>{formatTotals(pipeline.data.openTotals)}</Text>
                      </View>
                      <View style={styles.summaryItem}>
                        <Muted>本月赢单</Muted>
                        <Text style={[styles.amount, { color: p.text }]}>{formatTotals(pipeline.data.wonThisMonth)}</Text>
                      </View>
                    </View>
                  </Card>
                ) : null}
                <View style={styles.chips}>
                  {(Object.keys(crmDealStatus) as CrmDealStatus[]).map((s) => (
                    <Chip key={s} label={crmDealStatus[s]} active={status === s} onPress={() => setStatus(s)} />
                  ))}
                </View>
              </>
            ) : null}
          </View>
        }
        ListEmptyComponent={rows.isPending ? null : <Empty title={term ? '没有找到' : '这里还是空的'} hint={term ? '换个关键字试试。' : '点右上角新建，或者在对话里让智能体帮你录入。'} />}
        renderItem={({ item }) => (
          <Pressable onPress={() => router.push(item.href)} style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}>
            <View style={styles.cardTop}>
              <Text style={{ color: p.text, fontSize: 15, fontWeight: '500', flex: 1 }} numberOfLines={2}>
                {item.title}
              </Text>
              {item.right ? <Text style={{ color: p.text, fontSize: 15, fontWeight: '600' }}>{item.right}</Text> : null}
            </View>
            {item.lines.map((line) => (
              <Muted key={line}>{line}</Muted>
            ))}
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
  summary: { flexDirection: 'row', gap: 12 },
  summaryItem: { flex: 1, gap: 2 },
  amount: { fontSize: 17, fontWeight: '700' },
  headerButton: { paddingHorizontal: 12 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 4 },
  cardTop: { flexDirection: 'row', justifyContent: 'space-between', gap: 12 },
});
