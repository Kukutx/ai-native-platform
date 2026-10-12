import { Alert, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { LinkRow } from '@/components/crm';
import { OrderLines } from '@/components/erp';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { erpPurchaseOrderStatus, formatDate, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

type Transition = 'place' | 'receive' | 'close' | 'cancel';

export default function PurchaseOrderScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['erp', 'purchase-order', id], queryFn: ({ signal }) => getApi().erp.purchaseOrders.get(id, signal) });
  const transition = useMutation({
    mutationFn: (action: Transition) => getApi().erp.purchaseOrders[action](id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['erp'] }),
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const order = detail.data;
  const manage = sessionCan(session.data, 'erp.purchase');
  const started = order.status === 'partially_received' || order.status === 'received';
  const ask = (title: string, message: string, action: Transition) =>
    Alert.alert(title, message, [
      { text: '再想想', style: 'cancel' },
      { text: '确定', onPress: () => transition.mutate(action) },
    ]);

  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container}>
      <Stack.Screen options={{ title: order.number }} />
      <Text style={[styles.title, { color: p.text }]}>{order.supplierName ?? '供应商'}</Text>
      <Muted>
        {erpPurchaseOrderStatus[order.status]} · {order.expectedDate ? `预计 ${formatDate(order.expectedDate)} 到货` : `下单 ${formatDate(order.orderDate)}`} ·{' '}
        {order.warehouseName}
      </Muted>
      <OrderLines lines={order.lines} currency={order.currency} total={order.total} deliveredLabel={started ? '已收' : undefined} />
      {manage ? (
        <View style={styles.actions}>
          {order.status === 'draft' ? <Button title='下单' onPress={() => transition.mutate('place')} loading={transition.isPending} /> : null}
          {order.status === 'ordered' || order.status === 'partially_received' ? (
            <Button
              title={order.status === 'ordered' ? '全部收货入库' : '收下剩下的'}
              onPress={() => ask('收货入库', `货已到齐？把还没收的数量加到「${order.warehouseName}」。只收一部分请在网页端操作。`, 'receive')}
              loading={transition.isPending}
            />
          ) : null}
          {order.status === 'partially_received' ? (
            <Button title='结束收货' variant='outline' onPress={() => ask('结束收货', '剩下没到的不再收货，采购单记为已入库。', 'close')} disabled={transition.isPending} />
          ) : null}
          {order.status === 'draft' || order.status === 'ordered' ? (
            <Button title='取消' variant='outline' onPress={() => ask('取消采购单', '取消这张采购单？', 'cancel')} disabled={transition.isPending} />
          ) : null}
        </View>
      ) : null}
      <Card>
        {order.supplierId ? (
          <LinkRow title={order.supplierName ?? '供应商'} subtitle='供应商' href={{ pathname: '/crm/companies/[id]', params: { id: order.supplierId } }} />
        ) : null}
        <Muted>
          {order.createdBy.name ?? '系统'} 创建于 {formatTime(order.createdAt)}
        </Muted>
        {order.orderedAt ? <Muted>下单于 {formatTime(order.orderedAt)}</Muted> : null}
        {order.receivedAt ? <Muted>入库于 {formatTime(order.receivedAt)}</Muted> : null}
        {order.cancelledAt ? <Muted>取消于 {formatTime(order.cancelledAt)}</Muted> : null}
      </Card>
      {order.note ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{order.note}</Text> : null}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  actions: { gap: 8 },
});
