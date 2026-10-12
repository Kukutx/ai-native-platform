import { Pressable, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import type { ErpOrderLineDto } from '@ainative/api-client';
import { Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { erpSalesOrderStatus, formatAmount, formatDate, formatQuantity } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

/** 订单明细（只读）：每行产品、数量 × 单价、金额，最后是合计。deliveredLabel 有值时（“已发”“已收”）每行再显示交付进度。 */
export function OrderLines({ lines, currency, total, deliveredLabel }: { lines: ErpOrderLineDto[]; currency: string; total: number; deliveredLabel?: string }) {
  const p = usePalette();
  return (
    <Card>
      {lines.map((line) => (
        <Pressable
          key={line.productId}
          onPress={() => router.push({ pathname: '/erp/products/[id]', params: { id: line.productId } })}
          style={[styles.line, { borderBottomColor: p.border }]}
        >
          <View style={{ flex: 1 }}>
            <Text style={{ color: p.text, fontSize: 15 }} numberOfLines={1}>
              {line.productName}
            </Text>
            <Muted>
              {formatQuantity(line.quantity, line.unit)} × {formatAmount(line.unitPrice, currency)}
            </Muted>
            {deliveredLabel ? (
              <Muted>
                {deliveredLabel} {formatQuantity(line.delivered)}
                {line.returned ? ` · 已退 ${formatQuantity(line.returned)}` : ''}
              </Muted>
            ) : null}
          </View>
          <Text style={{ color: p.text, fontSize: 15 }}>{formatAmount(line.amount, currency)}</Text>
        </Pressable>
      ))}
      <View style={styles.total}>
        <Muted>合计</Muted>
        <Text style={{ color: p.text, fontSize: 18, fontWeight: '700' }}>{formatAmount(total, currency)}</Text>
      </View>
    </Card>
  );
}

/** 商机详情里的销售订单；没有订单时不显示。 */
export function DealOrders({ dealId }: { dealId: string }) {
  const p = usePalette();
  const orders = useQuery({
    queryKey: ['erp', 'sales-orders', { dealId }],
    queryFn: ({ signal }) => getApi().erp.salesOrders.list(undefined, undefined, undefined, dealId, undefined, 1, 20, undefined, signal),
  });
  if (!orders.data?.items.length) {
    return null;
  }

  return (
    <Card>
      <Text style={{ color: p.text, fontSize: 16, fontWeight: '600' }}>销售订单 {orders.data.total}</Text>
      {orders.data.items.map((o) => (
        <Pressable key={o.id} onPress={() => router.push({ pathname: '/erp/sales-orders/[id]', params: { id: o.id } })} style={styles.order}>
          <View style={{ flex: 1 }}>
            <Text style={{ color: p.text, fontSize: 15 }}>{o.number}</Text>
            <Muted>
              {erpSalesOrderStatus[o.status]} · {formatDate(o.orderDate)}
            </Muted>
          </View>
          <Text style={{ color: p.text, fontSize: 15 }}>{formatAmount(o.total, o.currency)}</Text>
        </Pressable>
      ))}
    </Card>
  );
}

const styles = StyleSheet.create({
  line: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 8, borderBottomWidth: StyleSheet.hairlineWidth },
  total: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', paddingTop: 4 },
  order: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 6 },
});
