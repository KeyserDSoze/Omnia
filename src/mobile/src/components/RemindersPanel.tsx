import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, TextInput, View } from 'react-native';
import { CloudApiError, type MemoryApi, type ReminderDto, type ReminderRepeat } from '../services/memoryApi';
import type { PushNotifications } from '../services/usePushNotifications';
import { checkReminder, deviceTimeZone, reminderLine } from '../services/reminders';
import { useT, type MessageKey } from '../i18n';
import { Button } from './Button';
import { web, type Theme } from './theme';

const repeatKey: Record<ReminderRepeat, MessageKey> = {
  once: 'reminders.repeatOnce', daily: 'reminders.repeatDaily', weekdays: 'reminders.repeatWeekdays'
};

/** Reminders that arrive as notifications on this device, even when the app is closed (ADR 0113). */
export function RemindersPanel({ api, push, theme: p }: { api: MemoryApi; push: PushNotifications; theme: Theme }) {
  const t = useT();
  const [reminders, setReminders] = useState<ReminderDto[]>([]);
  const [text, setText] = useState('');
  const [time, setTime] = useState('09:00');
  const [date, setDate] = useState('');
  const [repeat, setRepeat] = useState<ReminderRepeat>('daily');
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const live = useRef(true);

  const fail = (e: unknown) => { if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.saveFailed')); };

  const load = useCallback(async () => {
    try {
      const list = await api.reminders();
      if (live.current) setReminders(list);
    } catch (e) { fail(e); }
  }, [api]);

  useEffect(() => {
    live.current = true;
    void load();
    return () => { live.current = false; };
  }, [load]);

  async function add() {
    if (!checkReminder({ text, time, repeat, date })) { setMessage(t('reminders.invalid')); return; }
    setBusy(true);
    setMessage('');
    try {
      const created = await api.createReminder({
        text: text.trim(), repeat, time, timeZone: deviceTimeZone(), ...(repeat === 'once' ? { date } : {})
      });
      if (live.current) { setReminders(list => [...list, created]); setText(''); }
    } catch (e) { fail(e); } finally { if (live.current) setBusy(false); }
  }

  async function toggle(reminder: ReminderDto) {
    setBusy(true);
    try {
      await api.setReminderEnabled(reminder.id, !reminder.enabled);
      await load();
    } catch (e) { fail(e); } finally { if (live.current) setBusy(false); }
  }

  async function remove(reminder: ReminderDto) {
    setBusy(true);
    try {
      await api.deleteReminder(reminder.id);
      if (live.current) setReminders(list => list.filter(r => r.id !== reminder.id));
    } catch (e) { fail(e); } finally { if (live.current) setBusy(false); }
  }

  const card = [styles.card, { backgroundColor: p.surface, borderColor: p.border },
    web({ backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)' })];
  const input = [styles.input, { color: p.text, borderColor: p.border }];
  const status = push.support === 'unsupported' ? t('reminders.pushUnsupported') : push.support === 'denied' ? t('reminders.pushDenied')
    : push.on ? t('reminders.pushOn') : t('reminders.pushOff');

  return (
    <View testID="reminders-panel" style={card}>
      <Text style={[styles.kicker, { color: p.subtle }]}>{t('reminders.kicker')}</Text>
      <Text style={[styles.hint, { color: p.text }]}>{t('reminders.intro')}</Text>
      <Text testID="push-status" style={[styles.hint, { color: p.subtle }]}>{status}</Text>
      {push.support === 'ready' && (
        <View style={styles.row}>
          {push.on ? <>
            <Button testID="push-toggle" p={p} disabled={push.busy} label={t('reminders.disable')} onPress={() => { void push.disable(); }} />
            <Button testID="push-test" p={p} disabled={push.busy} label={t('reminders.test')} onPress={() => { void push.test(); }} />
          </> : <Button testID="push-toggle" p={p} primary disabled={push.busy || !push.loaded} label={t('reminders.enable')}
            onPress={() => { void push.enable(); }} />}
        </View>
      )}
      {!!push.message && <Text testID="push-message" accessibilityRole="alert" style={[styles.hint, { color: p.text }]}>{push.message}</Text>}

      {reminders.length === 0 ? <Text style={[styles.hint, { color: p.subtle }]}>{t('reminders.none')}</Text>
        : reminders.map(r => (
          <View key={r.id} testID="reminder-row" style={[styles.item, { borderColor: p.border }]}>
            <Text style={[styles.hint, { color: r.enabled ? p.text : p.subtle, marginTop: 0 }]}>
              {reminderLine(r, t(repeatKey[r.repeat]))}
            </Text>
            <View style={styles.row}>
              <Button testID="reminder-pause" p={p} disabled={busy} label={t(r.enabled ? 'reminders.pause' : 'reminders.resume')}
                onPress={() => { void toggle(r); }} />
              <Button testID="reminder-delete" p={p} disabled={busy} label={t('reminders.delete')} onPress={() => { void remove(r); }} />
            </View>
          </View>
        ))}

      <View style={[styles.item, { borderColor: p.border }]}>
        <TextInput testID="reminder-text" value={text} onChangeText={setText} maxLength={200} style={input}
          placeholder={t('reminders.textPlaceholder')} placeholderTextColor={p.subtle} />
        <View style={styles.row}>
          {(['daily', 'weekdays', 'once'] as const).map(kind => (
            <Button key={kind} testID={`reminder-repeat-${kind}`} p={p} selected={repeat === kind} label={t(repeatKey[kind])}
              onPress={() => setRepeat(kind)} />
          ))}
        </View>
        <View style={styles.row}>
          <TextInput testID="reminder-time" value={time} onChangeText={setTime} maxLength={5} style={[input, styles.short]}
            placeholder={t('reminders.timePlaceholder')} placeholderTextColor={p.subtle} />
          {repeat === 'once' && (
            <TextInput testID="reminder-date" value={date} onChangeText={setDate} maxLength={10} style={[input, styles.short]}
              placeholder={t('reminders.datePlaceholder')} placeholderTextColor={p.subtle} />
          )}
        </View>
        <View style={styles.row}>
          <Button testID="reminder-add" p={p} primary disabled={busy} label={t('reminders.add')} onPress={() => { void add(); }} />
        </View>
      </View>
      {!!message && <Text testID="reminders-message" accessibilityRole="alert" style={[styles.hint, { color: p.text }]}>{message}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 24, padding: 22 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.4 },
  hint: { fontSize: 13, lineHeight: 19, marginTop: 8 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 12 },
  item: { borderTopWidth: 1, paddingTop: 12, marginTop: 12 },
  input: { borderWidth: 1, borderRadius: 12, paddingHorizontal: 12, paddingVertical: 9, fontSize: 14 },
  short: { minWidth: 120 }
});
