import { useState } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { CompanyPicker } from '@/components/crm';
import { Button, Chip, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { usePalette } from '@/lib/theme';

type Kind = 'company' | 'contact' | 'deal';

const titles: Record<Kind, string> = { company: '新建客户', contact: '新建联系人', deal: '新建商机' };

/** 新建客户、联系人或商机。从详情页进来时会带上所属客户或联系人。 */
export default function NewCrmRecordScreen() {
  const p = usePalette();
  const params = useLocalSearchParams<{ type?: Kind; companyId?: string; companyName?: string; contactId?: string; contactName?: string }>();
  const kind: Kind = params.type === 'company' || params.type === 'contact' ? params.type : 'deal';
  const queryClient = useQueryClient();
  const [name, setName] = useState('');
  const [industry, setIndustry] = useState('');
  const [title, setTitle] = useState('');
  const [phone, setPhone] = useState('');
  const [email, setEmail] = useState('');
  const [amount, setAmount] = useState('');
  const [stageId, setStageId] = useState<string | undefined>();
  const [company, setCompany] = useState(params.companyId ? { id: params.companyId, name: params.companyName || '客户' } : null);
  const stages = useQuery({ queryKey: ['crm', 'stages'], queryFn: ({ signal }) => getApi().crm.stages.list(signal), enabled: kind === 'deal' });
  const create = useMutation({
    mutationFn: async () => {
      const api = getApi().crm;
      if (kind === 'company') {
        const created = await api.companies.create({ name: name.trim(), industry, phone, tags: [] });
        return { pathname: '/crm/companies/[id]' as const, id: created.id };
      }

      if (kind === 'contact') {
        const created = await api.contacts.create({ name: name.trim(), title, phone, email: email.trim(), companyId: company?.id, tags: [] });
        return { pathname: '/crm/contacts/[id]' as const, id: created.id };
      }

      const parsed = Number(amount.replace(/,/g, ''));
      const created = await api.deals.create({
        title: name.trim(),
        companyId: company?.id,
        contactId: params.contactId || undefined,
        stageId,
        amount: amount.trim() && Number.isFinite(parsed) ? parsed : undefined,
        currency: 'CNY',
        description: '',
      });
      return { pathname: '/crm/deals/[id]' as const, id: created.id };
    },
    onSuccess: async ({ pathname, id }) => {
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
      router.replace({ pathname, params: { id } });
    },
  });

  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
      <Stack.Screen options={{ title: titles[kind] }} />
      <Field
        label={kind === 'company' ? '名称' : kind === 'contact' ? '姓名' : '商机名称'}
        value={name}
        onChangeText={setName}
        autoCapitalize='sentences'
        maxLength={200}
        placeholder={kind === 'deal' ? '例如：星火科技年度采购' : undefined}
      />
      {kind === 'company' ? (
        <>
          <Field label='行业' value={industry} onChangeText={setIndustry} maxLength={100} />
          <Field label='电话' value={phone} onChangeText={setPhone} keyboardType='phone-pad' maxLength={50} />
        </>
      ) : null}
      {kind === 'contact' ? (
        <>
          <Field label='职位' value={title} onChangeText={setTitle} maxLength={100} />
          <Field label='电话' value={phone} onChangeText={setPhone} keyboardType='phone-pad' maxLength={50} />
          <Field label='邮箱' value={email} onChangeText={setEmail} keyboardType='email-address' maxLength={254} />
          <CompanyPicker value={company} onChange={setCompany} />
        </>
      ) : null}
      {kind === 'deal' ? (
        <>
          {params.contactId ? <Muted>联系人：{params.contactName}</Muted> : null}
          <CompanyPicker value={company} onChange={setCompany} />
          <Field label='预计金额（元）' value={amount} onChangeText={setAmount} keyboardType='decimal-pad' />
          <Text style={[styles.label, { color: p.text }]}>阶段</Text>
          <View style={styles.chips}>
            {stages.data
              ?.filter((s) => s.kind === 'open')
              .map((s, index) => (
                <Chip key={s.id} label={s.name} active={stageId ? stageId === s.id : index === 0} onPress={() => setStageId(s.id)} />
              ))}
          </View>
        </>
      ) : null}
      <Button title='创建' onPress={() => create.mutate()} loading={create.isPending} disabled={!name.trim()} style={styles.submit} />
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { padding: 16, gap: 14 },
  label: { fontSize: 14, fontWeight: '500' },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  submit: { marginTop: 8 },
});
