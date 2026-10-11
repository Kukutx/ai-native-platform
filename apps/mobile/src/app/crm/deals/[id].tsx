import { useState } from 'react';
import { Alert, KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { sessionCan, type CrmStageDto } from '@ainative/api-client';
import { ActivitySection, LinkRow } from '@/components/crm';
import { DealOrders } from '@/components/erp';
import { Card, Chip, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { crmDealStatus, formatAmount, formatDate, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function DealDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const [note, setNote] = useState('');
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['crm', 'deal', id], queryFn: ({ signal }) => getApi().crm.deals.get(id, signal) });
  const stages = useQuery({ queryKey: ['crm', 'stages'], queryFn: ({ signal }) => getApi().crm.stages.list(signal) });
  const move = useMutation({
    mutationFn: (stage: CrmStageDto) => getApi().crm.deals.move(id, { stageId: stage.id, note: note.trim() || undefined }),
    onSuccess: () => setNote(''),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['crm'] }),
  });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { deal, activities } = detail.data;
  const manage = sessionCan(session.data, 'crm.manage');
  const requestMove = (stage: CrmStageDto) => {
    if (stage.id === deal.stageId || move.isPending) {
      return;
    }

    if (stage.kind === 'open') {
      move.mutate(stage);
      return;
    }

    Alert.alert(`${stage.kind === 'won' ? '赢单' : '输单'}：${deal.title}`, note.trim() ? `说明：${note.trim()}` : '可以先在上面写下原因，会记入跟进记录。', [
      { text: '取消', style: 'cancel' },
      { text: '确定', onPress: () => move.mutate(stage) },
    ]);
  };

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: '商机' }} />
      <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
        <Text style={[styles.title, { color: p.text }]}>{deal.title}</Text>
        <Muted>
          {crmDealStatus[deal.status]} · {deal.stageName}
          {deal.status === 'open' ? ` · ${deal.probability}%` : ''}
        </Muted>
        <Card>
          <Text style={[styles.amount, { color: p.text }]}>{formatAmount(deal.amount, deal.currency)}</Text>
          <Muted>预计成交：{formatDate(deal.expectedCloseDate)}</Muted>
          <Muted>负责人：{deal.owner?.name ?? '—'}</Muted>
          {deal.closedAt ? <Muted>结束于：{formatTime(deal.closedAt)}</Muted> : null}
          {deal.companyId ? <LinkRow title={deal.companyName ?? '客户'} subtitle='客户' href={{ pathname: '/crm/companies/[id]', params: { id: deal.companyId } }} /> : null}
          {deal.contactId ? <LinkRow title={deal.contactName ?? '联系人'} subtitle='联系人' href={{ pathname: '/crm/contacts/[id]', params: { id: deal.contactId } }} /> : null}
        </Card>
        {deal.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{deal.description}</Text> : null}
        {manage && stages.data ? (
          <View style={styles.section}>
            <Text style={[styles.heading, { color: p.text }]}>推进阶段</Text>
            <Field label='推进说明（可选）' value={note} onChangeText={setNote} placeholder='例如赢单或输单的原因' autoCapitalize='sentences' />
            <View style={styles.chips}>
              {stages.data.map((stage) => (
                <Chip key={stage.id} label={stage.name} active={stage.id === deal.stageId} onPress={() => requestMove(stage)} />
              ))}
            </View>
          </View>
        ) : null}
        {sessionCan(session.data, 'erp.view') ? <DealOrders dealId={deal.id} /> : null}
        <ActivitySection scope={{ dealId: deal.id }} activities={activities} canLog={manage} />
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  amount: { fontSize: 24, fontWeight: '700' },
  section: { gap: 10 },
  heading: { fontSize: 16, fontWeight: '600', marginTop: 8 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
});
