import { useEffect, useRef, useState } from 'react';
import { Alert, FlatList, KeyboardAvoidingView, Platform, Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import { router, Stack, useLocalSearchParams } from 'expo-router';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import type { ChatMessageDto } from '@ainative/api-client';
import { Muted } from '@/components/ui';
import { getApi, streamChat } from '@/lib/api';
import { toolStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

interface Draft {
  user: string;
  text: string;
  tools: { callId: string; actionKey: string; status?: string }[];
}

type Row = { key: string; role: 'user' | 'assistant'; text: string; tools: { callId: string; actionKey: string; status?: string }[] };

function toRows(messages: ChatMessageDto[], draft: Draft | null): Row[] {
  const rows: Row[] = messages.map((m) => ({
    key: m.id,
    role: m.role === 'user' ? 'user' : 'assistant',
    text: m.text ?? '',
    tools: m.toolCalls.map((c) => ({ callId: c.callId, actionKey: c.actionKey, status: c.status })),
  }));
  if (draft) {
    rows.push({ key: 'draft-user', role: 'user', text: draft.user, tools: [] });
    rows.push({ key: 'draft-assistant', role: 'assistant', text: draft.text || '…', tools: draft.tools });
  }

  return rows;
}

function lastUserMessageIs(messages: ChatMessageDto[] | undefined, text: string) {
  return [...(messages ?? [])].reverse().find((m) => m.role === 'user')?.text === text;
}

/** 对话页。id 为 new 时是新对话：发送第一条消息时才创建会话。 */
export default function ChatScreen() {
  const p = usePalette();
  const insets = useSafeAreaInsets();
  const params = useLocalSearchParams<{ id: string }>();
  const queryClient = useQueryClient();
  const [conversationId, setConversationId] = useState(params.id === 'new' ? null : params.id);
  const [input, setInput] = useState('');
  const [draft, setDraft] = useState<Draft | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const listRef = useRef<FlatList<Row>>(null);
  const messages = useQuery({
    queryKey: ['messages', conversationId ?? 'new'],
    queryFn: ({ signal }) => getApi().conversations.messages(conversationId!, signal),
    enabled: !!conversationId,
  });

  useEffect(() => () => abortRef.current?.abort(), []);

  async function send() {
    const text = input.trim();
    if (!text || draft) {
      return;
    }

    setInput('');
    setDraft({ user: text, text: '', tools: [] });
    const controller = new AbortController();
    abortRef.current = controller;
    let id = conversationId;
    let failure: string | null = null;
    try {
      if (!id) {
        id = (await getApi().conversations.create({ title: text.slice(0, 40) }, controller.signal)).id;
        setConversationId(id);
        router.setParams({ id });
      }

      for await (const event of streamChat(id, text, controller.signal)) {
        if (event.type === 'text_delta') {
          setDraft((d) => d && { ...d, text: d.text + (event.text ?? '') });
        } else if (event.type === 'tool_call') {
          setDraft((d) => d && { ...d, tools: [...d.tools, { callId: event.callId!, actionKey: event.actionKey! }] });
        } else if (event.type === 'tool_result') {
          const status = (event.data as { status?: string } | undefined)?.status;
          setDraft((d) => d && { ...d, tools: d.tools.map((t) => (t.callId === event.callId ? { ...t, status } : t)) });
        } else if (event.type === 'error') {
          failure = event.text ?? '对话出错了';
        }
      }
    } catch (error) {
      if (!controller.signal.aborted) {
        failure = error instanceof Error ? error.message : '网络异常';
      }
    } finally {
      if (id) {
        await queryClient.invalidateQueries({ queryKey: ['messages', id] });
        await queryClient.invalidateQueries({ queryKey: ['conversations'] });
      }

      setDraft(null);
      if (failure) {
        // 消息没能保存（例如上一条还在回复中）时放回输入框，用户不用重新输入。
        if (!lastUserMessageIs(id ? queryClient.getQueryData<ChatMessageDto[]>(['messages', id]) : undefined, text)) {
          setInput((current) => current || text);
        }

        Alert.alert('发送失败', failure);
      }
    }
  }

  const rows = toRows(messages.data ?? [], draft);

  return (
    <KeyboardAvoidingView
      style={[styles.flex, { backgroundColor: p.background }]}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 90 : 0}
    >
      <Stack.Screen options={{ title: conversationId ? '对话' : '新对话' }} />
      <FlatList
        ref={listRef}
        data={rows}
        keyExtractor={(row) => row.key}
        contentContainerStyle={styles.list}
        onContentSizeChange={() => listRef.current?.scrollToEnd({ animated: true })}
        ListEmptyComponent={
          <View style={styles.empty}>
            <Text style={{ color: p.text, fontSize: 16, fontWeight: '600' }}>有什么可以帮你？</Text>
            <Muted style={{ marginTop: 6, textAlign: 'center' }}>试试：“帮我建一个任务：整理本周客户反馈，交给小智”</Muted>
          </View>
        }
        renderItem={({ item }) => (
          <View style={[styles.bubbleRow, item.role === 'user' ? styles.right : styles.left]}>
            <View style={[styles.bubble, { backgroundColor: item.role === 'user' ? p.primary : p.bubble }]}>
              {item.tools.length ? (
                <View style={styles.tools}>
                  {item.tools.map((t) => (
                    <Text key={t.callId} style={[styles.tool, { color: p.muted, borderColor: p.border }]}>
                      {t.actionKey}
                      {t.status ? ` · ${toolStatus[t.status] ?? t.status}` : ''}
                    </Text>
                  ))}
                </View>
              ) : null}
              <Text style={{ color: item.role === 'user' ? p.primaryText : p.text, fontSize: 15, lineHeight: 22 }}>{item.text}</Text>
            </View>
          </View>
        )}
      />
      <View style={[styles.composer, { borderColor: p.border, paddingBottom: Math.max(insets.bottom, 12) }]}>
        <TextInput
          value={input}
          onChangeText={setInput}
          placeholder='输入消息'
          placeholderTextColor={p.muted}
          multiline
          style={[styles.input, { color: p.text, borderColor: p.border, backgroundColor: p.card }]}
        />
        <Pressable
          accessibilityLabel={draft ? '停止' : '发送'}
          onPress={() => (draft ? abortRef.current?.abort() : void send())}
          disabled={!draft && !input.trim()}
          style={[styles.send, { backgroundColor: p.primary, opacity: !draft && !input.trim() ? 0.4 : 1 }]}
        >
          <Ionicons name={draft ? 'stop' : 'arrow-up'} size={20} color={p.primaryText} />
        </Pressable>
      </View>
    </KeyboardAvoidingView>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  list: { padding: 16, gap: 12, flexGrow: 1 },
  empty: { flex: 1, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 24 },
  bubbleRow: { flexDirection: 'row' },
  left: { justifyContent: 'flex-start' },
  right: { justifyContent: 'flex-end' },
  bubble: { maxWidth: '85%', borderRadius: 18, paddingHorizontal: 14, paddingVertical: 10, gap: 6 },
  tools: { flexDirection: 'row', flexWrap: 'wrap', gap: 4 },
  tool: { fontSize: 11, borderWidth: 1, borderRadius: 6, paddingHorizontal: 6, paddingVertical: 2, fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) },
  composer: { flexDirection: 'row', alignItems: 'flex-end', gap: 8, paddingHorizontal: 12, paddingTop: 10, borderTopWidth: StyleSheet.hairlineWidth },
  input: { flex: 1, maxHeight: 140, minHeight: 42, borderWidth: 1, borderRadius: 21, paddingHorizontal: 14, paddingTop: 10, paddingBottom: 10, fontSize: 15 },
  send: { width: 42, height: 42, borderRadius: 21, alignItems: 'center', justifyContent: 'center' },
});
