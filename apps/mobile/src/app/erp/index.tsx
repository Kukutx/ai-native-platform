import { useDeferredValue, useState } from 'react';
import { FlatList, Pressable, RefreshControl, StyleSheet, Text, TextInput, View } from 'react-native';
import { router, Stack } from 'expo-router';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import type { ErpPurchaseOrderStatus, ErpSalesOrderStatus } from '@ainative/api-client';
import { Card, Chip, Empty, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { erpPurchaseOrderStatus, erpSalesOrderStatus, formatAmount, formatDate, formatQuantity, formatTotals } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

type Segment = 'sales' | 'purchase' | 'stock';

interface Row {
  id: string;
  title: string;
  lines: string[];
  right?: string;
  warning?: boolean;
  href: Parameters<typeof router.push>[0];
}

const segments: { key: Segment; label: string }[] = [
  { key: 'sales', label: '销售订单' },
  { key: 'purchase', label: '采购订单' },
  { key: 'stock', label: '库存' },
];

export default function ErpScreen() {
  const p = usePalette();
  const [segment, setSegment] = useState<Segment>('sales');
  const [salesStatus, setSalesStatus] = useState<ErpSalesOrderStatus | undefined>('confirmed');
  const [purchaseStatus, setPurchaseStatus] = useState<ErpPurchaseOrderStatus | undefined>('ordered');
  const [lowOnly, setLowOnly] = useState(false);
  const [search, setSearch] = useState('');
  const term = useDeferredValue(search.trim()) || undefined;
  const summary = useQuery({ queryKey: ['erp', 'summary'], queryFn: ({ signal }) => getApi().erp.summary.get(signal) });
  const filter = segment === 'sales' ? salesStatus : segment === 'purchase' ? purchaseStatus : lowOnly;
  const rows = useQuery({
    queryKey: ['erp', segment, { search: term, filter }],
    queryFn: async ({ signal }): Promise<Row[]> => {
      const api = getApi().erp;
      if (segment === 'sales') {
        const page = await api.salesOrders.list(term, salesStatus, undefined, undefined, undefined, 1, 100, signal);
        return page.items.map((o) => ({
          id: o.id,
          title: `${o.number} · ${o.companyName ?? '未指定客户'}`,
          lines: [`${erpSalesOrderStatus[o.status]} · 下单 ${formatDate(o.orderDate)}`, o.dealTitle ? `商机：${o.dealTitle}` : ''].filter(Boolean),
          right: formatAmount(o.total, o.currency),
          href: { pathname: '/erp/sales-orders/[id]', params: { id: o.id } },
        }));
      }

      if (segment === 'purchase') {
        const page = await api.purchaseOrders.list(term, purchaseStatus, undefined, 1, 100, signal);
        return page.items.map((o) => ({
          id: o.id,
          title: `${o.number} · ${o.supplierName ?? '供应商'}`,
          lines: [`${erpPurchaseOrderStatus[o.status]} · ${o.expectedDate ? `预计 ${formatDate(o.expectedDate)} 到货` : `下单 ${formatDate(o.orderDate)}`}`],
          right: formatAmount(o.total, o.currency),
          href: { pathname: '/erp/purchase-orders/[id]', params: { id: o.id } },
        }));
      }

      const page = await api.products.list(term, undefined, lowOnly, undefined, 1, 100, signal);
      return page.items.map((product) => ({
        id: product.id,
        title: product.name,
        lines: [
          [product.sku, product.category].filter(Boolean).join(' · '),
          product.trackInventory ? `现有 ${formatQuantity(product.onHand)} · 已预留 ${formatQuantity(product.reserved)}` : '不管理库存',
        ],
        right: product.trackInventory ? `可用 ${formatQuantity(product.available, product.unit)}` : undefined,
        warning: product.lowStock,
        href: { pathname: '/erp/products/[id]', params: { id: product.id } },
      }));
    },
    placeholderData: keepPreviousData,
  });

  return (
    <View style={[styles.flex, { backgroundColor: p.background }]}>
      <Stack.Screen options={{ title: '进销存' }} />
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
              void summary.refetch();
            }}
          />
        }
        ListHeaderComponent={
          <View style={styles.header}>
            {summary.data ? (
              <Card>
                <View style={styles.summary}>
                  <View style={styles.summaryItem}>
                    <Muted>本月发货</Muted>
                    <Text style={[styles.amount, { color: p.text }]}>{formatTotals(summary.data.shippedThisMonth)}</Text>
                  </View>
                  <View style={styles.summaryItem}>
                    <Muted>待发货</Muted>
                    <Text style={[styles.amount, { color: p.text }]}>{summary.data.toShipSalesOrders} 单</Text>
                  </View>
                  <View style={styles.summaryItem}>
                    <Muted>需补货</Muted>
                    <Text style={[styles.amount, { color: summary.data.lowStockProducts ? p.warning : p.text }]}>{summary.data.lowStockProducts}</Text>
                  </View>
                </View>
              </Card>
            ) : null}
            <View style={styles.chips}>
              {segments.map((s) => (
                <Chip key={s.key} label={s.label} active={segment === s.key} onPress={() => setSegment(s.key)} />
              ))}
            </View>
            <TextInput
              value={search}
              onChangeText={setSearch}
              placeholder={segment === 'stock' ? '搜索产品名称或编码' : segment === 'sales' ? '搜索单号或客户' : '搜索单号或供应商'}
              placeholderTextColor={p.muted}
              style={[styles.search, { color: p.text, borderColor: p.border, backgroundColor: p.card }]}
              clearButtonMode='while-editing'
            />
            <View style={styles.chips}>
              {segment === 'sales' ? (
                <>
                  <Chip label='全部' active={!salesStatus} onPress={() => setSalesStatus(undefined)} />
                  {(Object.keys(erpSalesOrderStatus) as ErpSalesOrderStatus[]).map((s) => (
                    <Chip key={s} label={erpSalesOrderStatus[s]} active={salesStatus === s} onPress={() => setSalesStatus(s)} />
                  ))}
                </>
              ) : null}
              {segment === 'purchase' ? (
                <>
                  <Chip label='全部' active={!purchaseStatus} onPress={() => setPurchaseStatus(undefined)} />
                  {(Object.keys(erpPurchaseOrderStatus) as ErpPurchaseOrderStatus[]).map((s) => (
                    <Chip key={s} label={erpPurchaseOrderStatus[s]} active={purchaseStatus === s} onPress={() => setPurchaseStatus(s)} />
                  ))}
                </>
              ) : null}
              {segment === 'stock' ? (
                <>
                  <Chip label='全部产品' active={!lowOnly} onPress={() => setLowOnly(false)} />
                  <Chip label='需要补货' active={lowOnly} onPress={() => setLowOnly(true)} />
                </>
              ) : null}
            </View>
          </View>
        }
        ListEmptyComponent={rows.isPending ? null : <Empty title={term ? '没有找到' : '这里还是空的'} hint={term ? '换个关键字试试。' : '在网页端新建，或者在对话里让智能体帮你下单、录入。'} />}
        renderItem={({ item }) => (
          <Pressable onPress={() => router.push(item.href)} style={[styles.card, { backgroundColor: p.card, borderColor: p.border }]}>
            <View style={styles.cardTop}>
              <Text style={{ color: p.text, fontSize: 15, fontWeight: '500', flex: 1 }} numberOfLines={2}>
                {item.title}
              </Text>
              {item.right ? <Text style={{ color: item.warning ? p.warning : p.text, fontSize: 15, fontWeight: '600' }}>{item.right}</Text> : null}
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
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 4 },
  cardTop: { flexDirection: 'row', justifyContent: 'space-between', gap: 12 },
});
