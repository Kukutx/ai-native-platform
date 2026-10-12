import { useState } from 'react';
import { Linking, Pressable, StyleSheet, Text, View } from 'react-native';
import Ionicons from '@expo/vector-icons/Ionicons';
import { router } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { CrmActivityDto, CrmActivityKind } from '@ainative/api-client';
import { Button, Card, Chip, Field, Muted } from '@/components/ui';
import { getApi } from '@/lib/api';
import { crmActivityKind, formatTime } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

const LOGGABLE: CrmActivityKind[] = ['call', 'meeting', 'note', 'email'];

export interface TimelineScope {
  companyId?: string;
  contactId?: string;
  dealId?: string;
}

/** 跟进记录：顶部记录框（打完电话、开完会随手记一笔），下面是最近的往来。 */
export function ActivitySection({ scope, activities, canLog }: { scope: TimelineScope; activities: CrmActivityDto[]; canLog: boolean }) {
  const p = usePalette();
  const queryClient = useQueryClient();
  const [kind, setKind] = useState<CrmActivityKind>('call');
  const [content, setContent] = useState('');
  const log = useMutation({
    mutationFn: () => getApi().crm.activities.log({ kind, content: content.trim(), ...scope }),
    onSuccess: async () => {
      setContent('');
      await queryClient.invalidateQueries({ queryKey: ['crm'] });
    },
  });

  return (
    <View style={styles.section}>
      <Text style={[styles.heading, { color: p.text }]}>跟进记录</Text>
      {canLog ? (
        <Card>
          <View style={styles.chips}>
            {LOGGABLE.map((k) => (
              <Chip key={k} label={crmActivityKind[k]} active={kind === k} onPress={() => setKind(k)} />
            ))}
          </View>
          <Field label='内容' value={content} onChangeText={setContent} multiline placeholder='沟通了什么、对方反馈、下一步计划' autoCapitalize='sentences' />
          <Button title='记录' onPress={() => log.mutate()} loading={log.isPending} disabled={!content.trim()} />
        </Card>
      ) : null}
      {activities.length ? (
        activities.map((a) => (
          <Card key={a.id}>
            <Muted>
              {a.author.name ?? '系统'}
              {a.author.kind === 'agent' ? '（智能体）' : ''} · {crmActivityKind[a.kind]} · {formatTime(a.occurredAt)}
            </Muted>
            {a.dealTitle && a.dealId !== scope.dealId ? <Muted>商机：{a.dealTitle}</Muted> : null}
            <Text style={{ color: a.kind === 'stage_change' ? p.muted : p.text, fontSize: 15, lineHeight: 22 }}>{a.content}</Text>
          </Card>
        ))
      ) : (
        <Muted>还没有跟进记录。</Muted>
      )}
    </View>
  );
}

/** 一行可点的联系方式：电话直接拨号，邮箱打开邮件，官网打开浏览器。 */
export function ContactLine({ icon, value, url }: { icon: keyof typeof Ionicons.glyphMap; value?: string; url?: string }) {
  const p = usePalette();
  if (!value) {
    return null;
  }

  return (
    <Pressable onPress={url ? () => void Linking.openURL(url) : undefined} style={styles.line} accessibilityRole={url ? 'link' : undefined}>
      <Ionicons name={icon} size={18} color={p.muted} />
      <Text style={{ color: url ? p.text : p.muted, fontSize: 15, flex: 1, textDecorationLine: url ? 'underline' : 'none' }}>{value}</Text>
    </Pressable>
  );
}

/** 可点进详情的一行。 */
/**
 * 详情里的关联列表只带前一部分（后端上限 200 条），总数更多时提示。
 */
export function LimitNote({ shown, total }: { shown: number; total: number }) {
  return total > shown ? <Muted>只显示前 {shown} 个，共 {total} 个。</Muted> : null;
}

export function LinkRow({ title, subtitle, href }: { title: string; subtitle?: string; href: Parameters<typeof router.push>[0] }) {
  const p = usePalette();
  return (
    <Pressable onPress={() => router.push(href)} style={[styles.row, { borderColor: p.border }]}>
      <View style={{ flex: 1 }}>
        <Text style={{ color: p.text, fontSize: 15, fontWeight: '500' }} numberOfLines={1}>
          {title}
        </Text>
        {subtitle ? <Muted>{subtitle}</Muted> : null}
      </View>
      <Ionicons name='chevron-forward' size={18} color={p.muted} />
    </Pressable>
  );
}

/** 按名称搜索并选择客户（新建联系人、商机时用）。 */
export function CompanyPicker({ value, onChange }: { value: { id: string; name: string } | null; onChange: (value: { id: string; name: string } | null) => void }) {
  const p = usePalette();
  const [search, setSearch] = useState('');
  const term = search.trim();
  const results = useQuery({
    queryKey: ['crm', 'companies', { search: term, picker: true }],
    queryFn: ({ signal }) => getApi().crm.companies.list(term, undefined, undefined, 1, 6, signal),
    enabled: !value && term.length > 0,
  });

  if (value) {
    return (
      <View style={styles.section}>
        <Text style={[styles.label, { color: p.text }]}>客户</Text>
        <View style={styles.chips}>
          <Chip label={`${value.name}  ✕`} active onPress={() => onChange(null)} />
        </View>
      </View>
    );
  }

  return (
    <View style={styles.section}>
      <Field label='客户' value={search} onChangeText={setSearch} placeholder='输入名称搜索；可以不选' />
      <View style={styles.chips}>
        {results.data?.items.map((c) => (
          <Chip key={c.id} label={c.name} onPress={() => onChange({ id: c.id, name: c.name })} />
        ))}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  section: { gap: 10 },
  heading: { fontSize: 16, fontWeight: '600', marginTop: 8 },
  label: { fontSize: 14, fontWeight: '500' },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  line: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 4 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8, paddingVertical: 10, borderTopWidth: StyleSheet.hairlineWidth },
});
