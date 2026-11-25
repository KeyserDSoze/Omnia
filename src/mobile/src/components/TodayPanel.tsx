import { useEffect, useRef, useState, type ComponentRef } from 'react';
import { Animated, Linking, PanResponder, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, type ConnectorProviderDto, type TodayDto, type TriageDecision
} from '../services/memoryApi';
import type { TodaySource } from '../services/demoToday';
import {
  canApplyToSource, deckShortcut, decisionCounts, deckWindow, kindLabel, pendingCards, providerTaskApp, safeWebLink,
  tomorrowSnoozeMinutes, whenLabel, withDecision, withoutDecision, type DeckAction
} from '../services/todayDeck';
import { useT } from '../i18n';

export type Palette = {
  bg: string; surface: string; text: string; subtle: string; border: string;
  accent: string; accentText: string; card2: string;
};

const SWIPE_THRESHOLD = 110;
const kindIcons = { event: '◷', mail: '✉', task: '☐', chat: '💬' } as const;

/**
 * "Oggi": one card at a time, ordered by the server's deterministic priority. Swipe right =
 * done, left = not done; buttons do the same plus "later" (back in the queue after 2h), "tomorrow"
 * (back at 08:00) and "dismiss". With the card focused on the web: arrows, Del and Z (undo the last
 * decision). A provider write (completing a task) happens ONLY with the explicit checkbox, and a
 * decision that did it cannot be undone from here.
 */
export function TodayPanel({ api, providers, p, demo = false, suggestions = {} }: {
  api: TodaySource;
  providers: readonly ConnectorProviderDto[];
  p: Palette;
  demo?: boolean;
  /** AI pre-actions by item key (from the opt-in briefing); displayed, never executed. */
  suggestions?: Readonly<Record<string, string>>;
}) {
  const t = useT();
  const [snapshot, setSnapshot] = useState<TodayDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [applyToSource, setApplyToSource] = useState(false);
  const [now, setNow] = useState(() => new Date());
  const live = useRef(true);
  const position = useRef(new Animated.Value(0)).current;
  const rootRef = useRef<ComponentRef<typeof View>>(null);
  const decideRef = useRef<(decision: TriageDecision) => void>(() => { });
  const actRef = useRef<(action: DeckAction) => void>(() => { });
  // Decisions taken in this session, newest last: only these can be undone.
  const [history, setHistory] = useState<{ key: string; label: string; applied: boolean }[]>([]);

  async function load() {
    setLoading(true);
    setError('');
    const current = new Date();
    const range = deckWindow(current);
    try {
      const result = await api.today(range.from, range.to);
      if (!live.current) return;
      setSnapshot(result);
      setNow(current);
    } catch (e) {
      if (live.current) setError(e instanceof CloudApiError ? e.message : t('today.loadFailed'));
    } finally {
      if (live.current) setLoading(false);
    }
  }

  useEffect(() => {
    live.current = true;
    setHistory([]);
    void load();
    return () => { live.current = false; };
  }, [api]);

  // Keys act only while focus is inside this panel and not in a text field.
  useEffect(() => {
    if (Platform.OS !== 'web' || typeof document === 'undefined') return;
    const onKey = (event: KeyboardEvent) => {
      const root = rootRef.current as unknown as HTMLElement | null;
      const active = document.activeElement;
      if (!root || !active || !root.contains(active)) return;
      if (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement) return;
      const action = deckShortcut(event);
      if (!action) return;
      event.preventDefault();
      actRef.current(action);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const range = deckWindow(now);
  const items = snapshot?.items ?? [];
  const pending = pendingCards(items, now, range.dayStart);
  const card = pending[0] ?? null;
  const counts = decisionCounts(items);
  const troubled = (snapshot?.accounts ?? []).filter(a => a.status !== 'ok');
  const canApply = card !== null && canApplyToSource(card.item, providers);

  async function decide(decision: TriageDecision, queueForTomorrow = false) {
    if (!card || busy) return;
    setBusy(true);
    setError('');
    const apply = decision === 'done' && canApply && applyToSource;
    try {
      const result = await api.decide(card.key, decision, {
        applyToSource: apply,
        snoozeMinutes: decision !== 'later' ? undefined
          : queueForTomorrow ? tomorrowSnoozeMinutes(new Date()) : 120
      });
      if (!live.current) return;
      const decidedAt = new Date();
      setSnapshot(current => current === null ? current : {
        ...current,
        items: withDecision(current.items, result.key, result.decision, result.snoozedUntil,
          result.appliedToSource, decidedAt)
      });
      setHistory(h => [...h.slice(-9), {
        key: result.key, applied: result.appliedToSource,
        label: queueForTomorrow ? t('today.tomorrow') : decisionLabel(decision)
      }]);
      setNow(decidedAt);
      setApplyToSource(false);
    } catch (e) {
      if (live.current) setError(e instanceof CloudApiError ? e.message : t('today.decideFailed'));
    } finally {
      if (live.current) {
        setBusy(false);
        Animated.spring(position, { toValue: 0, useNativeDriver: false }).start();
      }
    }
  }
  decideRef.current = (decision) => { void decide(decision); };

  const decisionLabel = (decision: TriageDecision) => t(decision === 'done' ? 'today.done'
    : decision === 'not_done' ? 'today.notDone' : decision === 'later' ? 'today.later' : 'today.dismiss');
  const plain = (text: string) => text.replace(/^[^\p{L}\p{N}]+/u, '');
  const last = history[history.length - 1] ?? null;

  async function undo() {
    if (!last || last.applied || busy) return;
    setBusy(true);
    setError('');
    try {
      await api.undoDecision(last.key);
      if (!live.current) return;
      setSnapshot(current => current === null ? current : { ...current, items: withoutDecision(current.items, last.key) });
      setHistory(h => h.slice(0, -1));
      setNow(new Date());
    } catch (e) {
      if (live.current) setError(e instanceof CloudApiError ? e.message : t('today.decideFailed'));
    } finally {
      if (live.current) setBusy(false);
    }
  }
  actRef.current = action => {
    if (action === 'undo') void undo();
    else if (action === 'tomorrow') void decide('later', true);
    else void decide(action);
  };

  const pan = useRef(PanResponder.create({
    // Capture phase: a horizontal drag wins over text and buttons inside the card.
    onMoveShouldSetPanResponderCapture: (_, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy),
    onMoveShouldSetPanResponder: (_, g) => Math.abs(g.dx) > 12 && Math.abs(g.dx) > Math.abs(g.dy),
    onPanResponderMove: (_, g) => position.setValue(g.dx),
    onPanResponderRelease: (_, g) => {
      if (g.dx > SWIPE_THRESHOLD) decideRef.current('done');
      else if (g.dx < -SWIPE_THRESHOLD) decideRef.current('not_done');
      else Animated.spring(position, { toValue: 0, useNativeDriver: false }).start();
    },
    onPanResponderTerminate: () => Animated.spring(position, { toValue: 0, useNativeDriver: false }).start()
  })).current;

  const link = card ? safeWebLink(card.item.webLink) : null;
  const heading = { color: p.text };
  return (
    <View ref={rootRef} testID="today-panel" style={[styles.card, { backgroundColor: p.surface, borderColor: p.border }]}>
      <View style={styles.header}>
        <View style={{ flex: 1 }}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{demo ? t('today.kickerDemo') : t('today.kicker')}</Text>
          <Text style={[styles.title, heading]}>
            {card ? t('today.titleCard') : snapshot && snapshot.accounts.length > 0 ? t('today.titleClear') : t('today.title')}
          </Text>
        </View>
        <Pressable testID="today-reload" accessibilityRole="button" onPress={() => { void load(); }}
          disabled={loading || busy} style={[styles.smallButton, { backgroundColor: p.card2, opacity: loading ? 0.5 : 1 }]}>
          <Text style={[styles.smallText, heading]}>{loading ? t('today.loading') : t('today.reload')}</Text>
        </Pressable>
      </View>

      {troubled.map(account => (
        <View key={account.accountId} testID="today-account-warning" style={[styles.warning, { borderColor: p.border }]}>
          <Text style={[styles.hint, heading, { marginTop: 0 }]}>
            {t(account.status === 'reconnect_required' ? 'today.reconnect'
              : account.status === 'paused' ? 'today.paused' : 'today.unavailable', { account: account.displayName })}
          </Text>
        </View>
      ))}

      {snapshot && snapshot.accounts.length === 0 && (
        <Text testID="today-empty-accounts" style={[styles.hint, { color: p.subtle }]}>{t('today.empty')}</Text>
      )}

      {card && (
        <Animated.View testID="today-card" tabIndex={0} {...pan.panHandlers}
          style={[styles.deckCard, { backgroundColor: p.bg, borderColor: p.border,
            transform: [{ translateX: position }, { rotate: position.interpolate({
              inputRange: [-300, 0, 300], outputRange: ['-6deg', '0deg', '6deg'] }) }] }]}>
          <Text style={[styles.kind, { color: p.accent }]}>
            {kindIcons[card.item.kind]} {kindLabel(card.item.kind).toLocaleUpperCase()}
            {card.item.important ? ' · ' + t('today.important') : ''}{card.item.unread ? ' · ' + t('today.unread') : ''}
          </Text>
          <Text testID="today-card-title" style={[styles.cardTitle, heading]}>{card.item.title}</Text>
          <Text style={[styles.hint, { color: p.subtle }]}>
            {whenLabel(card.item, now)}{card.item.counterpart ? ' · ' + card.item.counterpart : ''}
          </Text>
          {!!card.item.detail && <Text style={[styles.detail, heading]}>{card.item.detail}</Text>}
          {!!suggestions[card.key] && (
            <View testID="today-pre-action" style={[styles.preAction, { borderColor: p.accent }]}>
              <Text style={[styles.kind, { color: p.accent }]}>{t('today.preAction')}</Text>
              <Text style={[styles.detail, heading, { marginTop: 4 }]}>{suggestions[card.key]}</Text>
            </View>
          )}
          {link && (
            <Pressable accessibilityRole="link" onPress={() => { void Linking.openURL(link); }}>
              <Text style={[styles.hint, { color: p.accent }]}>{t('today.openSource')}</Text>
            </Pressable>
          )}
          {canApply && (
            <Pressable testID="today-apply-source" accessibilityRole="checkbox"
              aria-checked={applyToSource} onPress={() => setApplyToSource(v => !v)}
              style={styles.checkRow}>
              <Text style={[styles.check, { borderColor: p.text, color: p.text }]}>{applyToSource ? '✓' : ' '}</Text>
              <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>
                {t('today.applySource', { app: providerTaskApp[card.item.provider] ?? card.item.provider })}
              </Text>
            </Pressable>
          )}
          <View style={styles.actions}>
            <Action testID="today-not-done" label={t('today.notDone')} onPress={() => { void decide('not_done'); }} p={p} disabled={busy} />
            <Action testID="today-later" label={t('today.later')} onPress={() => { void decide('later'); }} p={p} disabled={busy} />
            <Action testID="today-tomorrow" label={t('today.tomorrow')} onPress={() => { void decide('later', true); }} p={p} disabled={busy} />
            <Action testID="today-dismiss" label={t('today.dismiss')} onPress={() => { void decide('dismissed'); }} p={p} disabled={busy} />
            <Action testID="today-done" label={t('today.done')} onPress={() => { void decide('done'); }} p={p} disabled={busy} primary />
          </View>
          <Text style={[styles.hint, { color: p.subtle }]}>
            {Platform.OS === 'web' ? t('today.dragHint') + ' ' : ''}{t('today.queue', { count: pending.length })}
          </Text>
          {Platform.OS === 'web' && (
            <Text testID="today-shortcuts" style={[styles.hint, { color: p.subtle }]}>
              {'→ ' + plain(t('today.done')) + ' · ← ' + plain(t('today.notDone')) + ' · ↓ ' + plain(t('today.later')) +
                ' · ↑ ' + plain(t('today.tomorrow')) + ' · Del ' + plain(t('today.dismiss')) + ' · Z ' + t('today.undo')}
            </Text>
          )}
        </Animated.View>
      )}

      {last && !last.applied && (
        <View style={styles.undoRow}>
          <Text style={[styles.hint, { color: p.subtle, flex: 1, marginTop: 0 }]}>
            {t('today.lastDecision', { decision: plain(last.label) })}
          </Text>
          <Action testID="today-undo" label={t('today.undo')} onPress={() => { void undo(); }} p={p} disabled={busy} />
        </View>
      )}

      {snapshot && !card && snapshot.accounts.length > 0 && (
        <Text testID="today-clear" style={[styles.hint, heading]}>{t('today.clear')}</Text>
      )}
      {snapshot && items.length > 0 && (
        <Text testID="today-summary" style={[styles.hint, { color: p.subtle }]}>
          {t('today.summary', { done: counts.done, notDone: counts.not_done, later: counts.later, dismissed: counts.dismissed })}
        </Text>
      )}
      {!!error && <Text accessibilityRole="alert" testID="today-error" style={[styles.hint, heading]}>{error}</Text>}
    </View>
  );
}

function Action({ label, onPress, p, disabled, primary, testID }: {
  label: string; onPress: () => void; p: Palette; disabled: boolean; primary?: boolean; testID: string;
}) {
  return (
    <Pressable testID={testID} accessibilityRole="button" onPress={onPress} disabled={disabled}
      style={[styles.action, { backgroundColor: primary ? p.accent : p.card2, opacity: disabled ? 0.5 : 1 }]}>
      <Text style={[styles.smallText, { color: primary ? p.accentText : p.text }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 25, padding: 26, marginBottom: 20 },
  header: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.2 },
  title: { fontSize: 25, fontWeight: '700', letterSpacing: -0.8, marginTop: 12 },
  smallButton: { minHeight: 40, paddingHorizontal: 14, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  smallText: { fontSize: 14, fontWeight: '700' },
  warning: { borderWidth: 1, borderRadius: 12, padding: 10, marginTop: 12 },
  deckCard: { borderWidth: 1, borderRadius: 20, padding: 20, marginTop: 16, userSelect: 'none', cursor: 'grab' },
  kind: { fontSize: 11, fontWeight: '800', letterSpacing: 1 },
  cardTitle: { fontSize: 21, fontWeight: '700', marginTop: 8, lineHeight: 27 },
  detail: { fontSize: 14, lineHeight: 21, marginTop: 10 },
  preAction: { borderLeftWidth: 3, paddingLeft: 10, marginTop: 12 },
  hint: { fontSize: 12, lineHeight: 18, marginTop: 8 },
  checkRow: { flexDirection: 'row', alignItems: 'center', gap: 10, marginTop: 14 },
  check: { width: 22, height: 22, borderWidth: 1.5, borderRadius: 6, textAlign: 'center', fontWeight: '800', lineHeight: 19 },
  actions: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 16 },
  undoRow: { flexDirection: 'row', alignItems: 'center', gap: 12, marginTop: 14 },
  action: { minHeight: 44, paddingHorizontal: 14, borderRadius: 12, alignItems: 'center', justifyContent: 'center', flexGrow: 1 }
});
