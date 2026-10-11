import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { erpMovementKind, formatAmount, formatQuantity, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ProductScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const detail = useQuery({ queryKey: ['erp', 'product', id], queryFn: ({ signal }) => getApi().erp.products.get(id, signal) });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { product, levels, movements } = detail.data;
  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container}>
      <Stack.Screen options={{ title: product.sku }} />
      <Text style={[styles.title, { color: p.text }]}>{product.name}</Text>
      <Muted>
        {[product.sku, product.category, product.isActive ? '' : '停售'].filter(Boolean).join(' · ')}
      </Muted>
      <Card>
        <Muted>售价 {formatAmount(product.price, product.currency)} · 采购价 {formatAmount(product.cost, product.currency)}</Muted>
        {product.trackInventory ? (
          <>
            <Text style={[styles.amount, { color: product.lowStock ? p.warning : p.text }]}>可用 {formatQuantity(product.available, product.unit)}</Text>
            <Muted>
              现有 {formatQuantity(product.onHand)} · 已预留 {formatQuantity(product.reserved)}
              {product.reorderPoint !== undefined ? ` · 提醒线 ${formatQuantity(product.reorderPoint)}` : ''}
            </Muted>
          </>
        ) : (
          <Muted>不管理库存</Muted>
        )}
      </Card>
      {levels.length ? (
        <Card>
          <Text style={[styles.heading, { color: p.text }]}>各仓库</Text>
          {levels.map((l) => (
            <View key={l.warehouseId} style={styles.row}>
              <Text style={{ color: p.text, flex: 1 }}>{l.warehouseName}</Text>
              <Muted>
                现有 {formatQuantity(l.onHand)} · 可用 {formatQuantity(l.available)}
              </Muted>
            </View>
          ))}
        </Card>
      ) : null}
      {movements.length ? (
        <Card>
          <Text style={[styles.heading, { color: p.text }]}>最近流水</Text>
          {movements.map((m) => (
            <View key={m.id} style={styles.movement}>
              <View style={styles.row}>
                <Text style={{ color: p.text, flex: 1 }}>
                  {erpMovementKind[m.kind]} · {m.warehouseName}
                </Text>
                <Text style={{ color: m.quantity > 0 ? p.success : p.text, fontWeight: '600' }}>
                  {m.quantity > 0 ? '+' : ''}
                  {formatQuantity(m.quantity)}
                </Text>
              </View>
              <Muted>
                {formatTime(m.createdAt)} · 结存 {formatQuantity(m.balanceAfter)}
                {m.salesOrderNumber ? ` · ${m.salesOrderNumber}` : ''}
                {m.purchaseOrderNumber ? ` · ${m.purchaseOrderNumber}` : ''}
              </Muted>
              {m.note ? <Muted>{m.note}</Muted> : null}
            </View>
          ))}
        </Card>
      ) : null}
      {product.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{product.description}</Text> : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  amount: { fontSize: 22, fontWeight: '700' },
  heading: { fontSize: 16, fontWeight: '600' },
  row: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 8 },
  movement: { gap: 2, paddingVertical: 4 },
});
