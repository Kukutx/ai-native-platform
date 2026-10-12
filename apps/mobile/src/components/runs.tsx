import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import { router } from 'expo-router';
import type { RunDto, RunEventDto } from '@ainative/api-client';
import { Muted, Status } from '@/components/ui';
import { formatTime, formatUsd, runEventType, runStatus, toolStatus } from '@/lib/labels';
import { usePalette } from '@/lib/theme';

/** 执行记录列表里的一行：状态、总结（或错误）、步数、花费和开始时间。 */
export function RunRow({ run, showAgent }: { run: RunDto; showAgent?: boolean }) {
  const p = usePalette();
  const status = runStatus[run.status];
  return (
    <Pressable
      onPress={() => router.push({ pathname: '/runs/[id]', params: { id: run.id } })}
      accessibilityRole='button'
      style={({ pressed }) => [styles.card, { backgroundColor: p.card, borderColor: p.border }, pressed && { opacity: 0.6 }]}
    >
      <View style={styles.row}>
        {showAgent ? (
          <Text style={{ color: p.text, fontSize: 15, fontWeight: '600', flex: 1 }} numberOfLines={1}>
            {run.agentName}
          </Text>
        ) : (
          <View style={styles.flex} />
        )}
        <Status label={status.label} tone={status.tone} />
      </View>
      <Text style={{ color: run.error ? p.danger : p.text, fontSize: 14, lineHeight: 20 }} numberOfLines={2}>
        {run.error ?? run.summary ?? '查看过程'}
      </Text>
      <Muted>
        {run.steps} 步 · {formatUsd(run.costUsd)} · {formatTime(run.startedAt ?? run.createdAt)}
      </Muted>
    </Pressable>
  );
}

type EventData = Record<string, unknown>;

function Json({ value }: { value: unknown }) {
  const p = usePalette();
  if (value === undefined || value === null) {
    return null;
  }

  return (
    <Text style={[styles.code, { color: p.muted, backgroundColor: p.bubble }]} numberOfLines={12}>
      {JSON.stringify(value, null, 2)}
    </Text>
  );
}

function ActionChip({ actionKey, status }: { actionKey: string; status?: string }) {
  const p = usePalette();
  return (
    <Text style={[styles.action, { color: p.muted, borderColor: p.border }]}>
      {actionKey}
      {status ? ` · ${toolStatus[status] ?? status}` : ''}
    </Text>
  );
}

function EventBody({ event, onOpenApprovals }: { event: RunEventDto; onOpenApprovals?: () => void }) {
  const p = usePalette();
  const data = (event.data ?? {}) as EventData;
  switch (event.type) {
    case 'status': {
      const status = typeof data.status === 'string' ? runStatus[data.status as keyof typeof runStatus] : undefined;
      const reasons = Array.isArray(data.reasons) ? (data.reasons as unknown[]).map(String) : [];
      return (
        <View style={styles.body}>
          <Text style={{ color: p.text, fontWeight: '600' }}>{status?.label ?? (typeof data.status === 'string' ? data.status : '状态变化')}</Text>
          {reasons.map((reason, index) => (
            <Muted key={index}>· {reason}</Muted>
          ))}
          {typeof data.summary === 'string' ? <Text style={[styles.text, { color: p.text }]}>{data.summary}</Text> : null}
        </View>
      );
    }
    case 'assistant_text':
      return <Text style={[styles.text, { color: p.text }]}>{String(data.text ?? '')}</Text>;
    case 'tool_call':
      return (
        <View style={styles.body}>
          <ActionChip actionKey={String(data.actionKey ?? '')} />
          <Json value={data.arguments} />
        </View>
      );
    case 'tool_result':
    case 'approval_requested':
      return (
        <View style={styles.body}>
          <ActionChip actionKey={String(data.actionKey ?? '')} status={typeof data.status === 'string' ? data.status : undefined} />
          {typeof data.error === 'string' ? <Text style={{ color: p.danger }}>{data.error}</Text> : null}
          {event.type === 'approval_requested' ? (
            <Text style={{ color: p.text }}>
              已提交人工审批
              {onOpenApprovals ? (
                <Text style={{ textDecorationLine: 'underline' }} onPress={onOpenApprovals}>
                  ，去处理
                </Text>
              ) : null}
            </Text>
          ) : (
            <Json value={data.output} />
          )}
        </View>
      );
    case 'error':
      return <Text style={[styles.text, { color: p.danger }]}>{String(data.error ?? '执行失败')}</Text>;
    default:
      return <Json value={data} />;
  }
}

/** 运行过程时间线：持久化事件按序号排列，末尾附上还没落库的逐字输出。 */
export function RunTimeline({ events, liveText, onOpenApprovals }: { events: RunEventDto[]; liveText?: string; onOpenApprovals?: () => void }) {
  const p = usePalette();
  return (
    <View>
      {events.map((event, index) => (
        <View key={event.seq} style={styles.item}>
          <View style={styles.rail}>
            <View style={[styles.dot, { borderColor: event.type === 'error' ? p.danger : p.muted, backgroundColor: p.background }]} />
            {index < events.length - 1 || liveText ? <View style={[styles.line, { backgroundColor: p.border }]} /> : null}
          </View>
          <View style={styles.content}>
            <Muted>
              {runEventType[event.type] ?? event.type} · #{event.seq} · {formatTime(event.createdAt)}
            </Muted>
            <EventBody event={event} onOpenApprovals={onOpenApprovals} />
          </View>
        </View>
      ))}
      {liveText ? (
        <View style={styles.item}>
          <View style={styles.rail}>
            <View style={[styles.dot, { borderColor: p.primary, backgroundColor: p.primary }]} />
          </View>
          <View style={styles.content}>
            <Muted>正在输出…</Muted>
            <Text style={[styles.text, { color: p.text }]}>{liveText}</Text>
          </View>
        </View>
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create({
  flex: { flex: 1 },
  card: { borderWidth: 1, borderRadius: 14, padding: 14, gap: 6 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  item: { flexDirection: 'row', gap: 10 },
  rail: { width: 12, alignItems: 'center' },
  dot: { width: 10, height: 10, borderRadius: 5, borderWidth: 2, marginTop: 4 },
  line: { flex: 1, width: StyleSheet.hairlineWidth * 2, marginTop: 2 },
  content: { flex: 1, gap: 4, paddingBottom: 16 },
  body: { gap: 6 },
  text: { fontSize: 15, lineHeight: 22 },
  action: {
    alignSelf: 'flex-start',
    fontSize: 12,
    borderWidth: 1,
    borderRadius: 6,
    paddingHorizontal: 6,
    paddingVertical: 2,
    fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }),
  },
  code: { fontSize: 12, padding: 10, borderRadius: 8, fontFamily: Platform.select({ ios: 'Menlo', default: 'monospace' }) },
});
