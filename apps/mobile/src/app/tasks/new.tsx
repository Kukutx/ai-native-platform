import { useState } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { WorkItemPriority } from '@ainative/api-client';
import { Button, Chip, Field } from '@/components/ui';
import { getApi } from '@/lib/api';
import { priority } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

export default function NewTaskScreen() {
  const p = usePalette();
  const queryClient = useQueryClient();
  const agents = useQuery({ queryKey: ['agents'], queryFn: ({ signal }) => getApi().agents.list(signal) });
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [level, setLevel] = useState<WorkItemPriority>('medium');
  const [assignee, setAssignee] = useState<string | undefined>();
  const create = useMutation({
    mutationFn: () => getApi().workItems.create({ title: title.trim(), description, priority: level, assigneeAgentId: assignee }),
    onSuccess: async (task) => {
      await queryClient.invalidateQueries({ queryKey: ['work-items'] });
      router.replace({ pathname: '/tasks/[id]', params: { id: task.id } });
    },
  });

  return (
    <ScrollView style={{ backgroundColor: p.background }} contentContainerStyle={styles.container} keyboardShouldPersistTaps='handled'>
      <Field label='标题' value={title} onChangeText={setTitle} autoCapitalize='sentences' maxLength={300} />
      <Field label='描述' value={description} onChangeText={setDescription} multiline autoCapitalize='sentences' />
      <Text style={[styles.label, { color: p.text }]}>优先级</Text>
      <View style={styles.chips}>
        {(Object.keys(priority) as WorkItemPriority[]).map((key) => (
          <Chip key={key} label={priority[key]} active={level === key} onPress={() => setLevel(key)} />
        ))}
      </View>
      <Text style={[styles.label, { color: p.text }]}>指派给智能体</Text>
      <View style={styles.chips}>
        <Chip label='暂不指派' active={!assignee} onPress={() => setAssignee(undefined)} />
        {agents.data
          ?.filter((a) => a.status !== 'terminated')
          .map((a) => (
            <Chip key={a.id} label={a.name} active={assignee === a.id} onPress={() => setAssignee(a.id)} />
          ))}
      </View>
      <Button title='创建' onPress={() => create.mutate()} loading={create.isPending} disabled={!title.trim()} style={styles.submit} />
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { padding: 16, gap: 14 },
  label: { fontSize: 14, fontWeight: '500' },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  submit: { marginTop: 8 },
});
