import { Alert, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { LinkRow } from '@/components/crm';
import { OrderLines } from '@/components/erp';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { erpSalesOrderStatus, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

type Transition = 'confirm' | 'ship' | 'close' | 'cancel';

export default function SalesOrderScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['erp', 'sales-order', id], queryFn: ({ signal }) => getApi().erp.salesOrders.get(id, signal) });
  const transition = useMutation({
    mutationFn: (action: Transition) => getApi().erp.salesOrders[action](id),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['erp'] }),
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const order = detail.data;
  const manage = sessionCan(session.data, 'erp.sales');
  const started = order.status === 'partially_shipped' || order.status === 'shipped';
  const ask = (title: string, message: string, action: Transition) =>
    Alert.alert(title, message, [
      { text: '再想想', style: 'cancel' },
      { text: '确定', onPress: () => transition.mutate(action) },
    ]);

  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container}>
      <Stack.Screen options={{ title: order.number }} />
      <Text style={[styles.title, { color: p.text }]}>{order.companyName ?? '未指定客户'}</Text>
      <Muted>
        {erpSalesOrderStatus[order.status]} · 下单 {formatDate(order.orderDate)} · {order.warehouseName}
      </Muted>
      <OrderLines lines={order.lines} currency={order.currency} total={order.total} deliveredLabel={started ? '已发' : undefined} />
      {manage ? (
        <View style={styles.actions}>
          {order.status === 'draft' ? (
            <Button title='确认订单（预留库存）' onPress={() => transition.mutate('confirm')} loading={transition.isPending} />
          ) : null}
          {order.status === 'confirmed' || order.status === 'partially_shipped' ? (
            <Button
              title={order.status === 'confirmed' ? '全部发货' : '发出剩下的'}
              onPress={() => ask('发货', `从「${order.warehouseName}」发出还没发的全部数量。只发一部分请在网页端操作。`, 'ship')}
              loading={transition.isPending}
            />
          ) : null}
          {order.status === 'partially_shipped' ? (
            <Button
              title='结束发货'
              variant='outline'
              onPress={() => ask('结束发货', '剩下没发的不再发货，释放预留的库存。', 'close')}
              disabled={transition.isPending}
            />
          ) : null}
          {order.status === 'draft' || order.status === 'confirmed' ? (
            <Button
              title='取消订单'
              variant='outline'
              onPress={() => ask('取消订单', order.status === 'confirmed' ? '取消后释放预留的库存。' : '取消这张草稿订单？', 'cancel')}
              disabled={transition.isPending}
            />
          ) : null}
        </View>
      ) : null}
      <Card>
        {order.companyId ? <LinkRow title={order.companyName ?? '客户'} subtitle='客户' href={{ pathname: '/crm/companies/[id]', params: { id: order.companyId } }} /> : null}
        {order.contactId ? <LinkRow title={order.contactName ?? '联系人'} subtitle='联系人' href={{ pathname: '/crm/contacts/[id]', params: { id: order.contactId } }} /> : null}
        {order.dealId ? <LinkRow title={order.dealTitle ?? '商机'} subtitle='商机' href={{ pathname: '/crm/deals/[id]', params: { id: order.dealId } }} /> : null}
        <Muted>负责人：{order.owner?.name ?? '—'}</Muted>
        {started ? (
          <Muted>
            已发货 {formatAmount(order.shippedAmount, order.currency)}
            {order.grossProfit !== undefined ? ` · 毛利 ${formatAmount(order.grossProfit, order.currency)}` : ''}
          </Muted>
        ) : null}
        <Muted>
          {order.createdBy.name ?? '系统'} 创建于 {formatTime(order.createdAt)}
        </Muted>
        {order.confirmedAt ? <Muted>确认于 {formatTime(order.confirmedAt)}</Muted> : null}
        {order.shippedAt ? <Muted>发货于 {formatTime(order.shippedAt)}</Muted> : null}
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
