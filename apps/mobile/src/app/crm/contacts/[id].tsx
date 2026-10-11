import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { ActivitySection, ContactLine, LimitNote, LinkRow } from '@/components/crm';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { crmDealStatus, formatAmount } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function ContactDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['crm', 'contact', id], queryFn: ({ signal }) => getApi().crm.contacts.get(id, signal) });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { contact, deals, activities, dealTotal } = detail.data;
  const manage = sessionCan(session.data, 'crm.manage');

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: '联系人' }} />
      <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
        <Text style={[styles.title, { color: p.text }]}>{contact.name}</Text>
        {contact.title ? <Muted>{contact.title}</Muted> : null}
        <Card>
          <ContactLine icon='call-outline' value={contact.phone} url={contact.phone ? `tel:${contact.phone}` : undefined} />
          <ContactLine icon='mail-outline' value={contact.email} url={contact.email ? `mailto:${contact.email}` : undefined} />
          {contact.companyId ? (
            <LinkRow title={contact.companyName ?? '客户'} subtitle='所属客户' href={{ pathname: '/crm/companies/[id]', params: { id: contact.companyId } }} />
          ) : null}
          <Muted>负责人：{contact.owner?.name ?? '—'}</Muted>
          {contact.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{contact.description}</Text> : null}
        </Card>
        <Card>
          <Text style={[styles.heading, { color: p.text }]}>商机 {dealTotal || ''}</Text>
          {deals.map((d) => (
            <LinkRow
              key={d.id}
              title={d.title}
              subtitle={`${crmDealStatus[d.status]} · ${d.stageName} · ${formatAmount(d.amount, d.currency)}`}
              href={{ pathname: '/crm/deals/[id]', params: { id: d.id } }}
            />
          ))}
          <LimitNote shown={deals.length} total={dealTotal} />
          {manage ? (
            <Button
              title='添加商机'
              variant='outline'
              onPress={() =>
                router.push({
                  pathname: '/crm/new',
                  params: { type: 'deal', contactId: contact.id, contactName: contact.name, companyId: contact.companyId ?? '', companyName: contact.companyName ?? '' },
                })
              }
            />
          ) : null}
        </Card>
        <ActivitySection scope={{ contactId: contact.id }} activities={activities} canLog={manage} />
      </ScrollView>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  container: { padding: 16, gap: 12 },
  title: { fontSize: 22, fontWeight: '700' },
  heading: { fontSize: 16, fontWeight: '600' },
});
