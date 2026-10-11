import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useQuery } from '@tanstack/react-query';
import { sessionCan } from '@ainative/api-client';
import { ActivitySection, ContactLine, LimitNote, LinkRow } from '@/components/crm';
import { Button, Card, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { crmDealStatus, formatAmount } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function CompanyDetailScreen() {
  const p = usePalette();
  const { id } = useLocalSearchParams<{ id: string }>();
  const session = useQuery({ queryKey: ['session'], queryFn: ({ signal }) => getApi().auth.session(signal) });
  const detail = useQuery({ queryKey: ['crm', 'company', id], queryFn: ({ signal }) => getApi().crm.companies.get(id, signal) });

  if (!detail.data) {
    return <View style={[styles.flex, { backgroundColor: p.background }]} />;
  }

  const { company, contacts, deals, activities, contactTotal, dealTotal } = detail.data;
  const manage = sessionCan(session.data, 'crm.manage');
  const website = company.website && (/^https?:\/\//i.test(company.website) ? company.website : `https://${company.website}`);

  return (
    <KeyboardAvoidingView style={[styles.flex, { backgroundColor: p.background }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <Stack.Screen options={{ title: '客户' }} />
      <ScrollView contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
        <Text style={[styles.title, { color: p.text }]}>{company.name}</Text>
        {company.industry || company.tags.length ? <Muted>{[company.industry, ...company.tags].filter(Boolean).join(' · ')}</Muted> : null}
        <Card>
          <ContactLine icon='call-outline' value={company.phone} url={company.phone ? `tel:${company.phone}` : undefined} />
          <ContactLine icon='globe-outline' value={company.website} url={website || undefined} />
          <ContactLine icon='location-outline' value={company.address} />
          <Muted>负责人：{company.owner?.name ?? '—'}</Muted>
          {company.description ? <Text style={{ color: p.text, fontSize: 15, lineHeight: 22 }}>{company.description}</Text> : null}
        </Card>
        <Card>
          <Text style={[styles.heading, { color: p.text }]}>联系人 {contactTotal || ''}</Text>
          {contacts.map((c) => (
            <LinkRow
              key={c.id}
              title={c.name}
              subtitle={[c.title, c.phone].filter(Boolean).join(' · ') || undefined}
              href={{ pathname: '/crm/contacts/[id]', params: { id: c.id } }}
            />
          ))}
          <LimitNote shown={contacts.length} total={contactTotal} />
          {manage ? (
            <Button
              title='添加联系人'
              variant='outline'
              onPress={() => router.push({ pathname: '/crm/new', params: { type: 'contact', companyId: company.id, companyName: company.name } })}
            />
          ) : null}
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
              onPress={() => router.push({ pathname: '/crm/new', params: { type: 'deal', companyId: company.id, companyName: company.name } })}
            />
          ) : null}
        </Card>
        <ActivitySection scope={{ companyId: company.id }} activities={activities} canLog={manage} />
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
