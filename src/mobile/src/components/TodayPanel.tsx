import { useEffect, useRef, useState } from 'react';
import { Animated, Linking, PanResponder, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, type ConnectorProviderDto, type TodayDto, type TriageDecision
} from '../services/memoryApi';
import type { TodaySource } from '../services/demoToday';
import {
  canApplyToSource, decisionCounts, deckWindow, kindLabels, pendingCards, providerTaskApp, safeWebLink,
  whenLabel, withDecision
} from '../services/todayDeck';

export type Palette = {
  bg: string; surface: string; text: string; subtle: string; border: string;
  accent: string; accentText: string; card2: string;
};

const SWIPE_THRESHOLD = 110;
const kindIcons = { event: '◷', mail: '✉', task: '☐', chat: '💬' } as const;

/**
 * "Oggi": one card at a time, ordered by the server's deterministic priority. Swipe right =
 * done, left = not done; buttons do the same plus "later" (back in the queue after 2h) and
 * "dismiss". A provider write (completing a task) happens ONLY with the explicit checkbox.
 */
export function TodayPanel({ api, providers, p, demo = false, suggestions = {} }: {
  api: TodaySource;
  providers: readonly ConnectorProviderDto[];
  p: Palette;
  demo?: boolean;
  /** AI pre-actions by item key (from the opt-in briefing); displayed, never executed. */
  suggestions?: Readonly<Record<string, string>>;
}) {
  const [snapshot, setSnapshot] = useState<TodayDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [applyToSource, setApplyToSource] = useState(false);
  const [now, setNow] = useState(() => new Date());
  const live = useRef(true);
  const position = useRef(new Animated.Value(0)).current;
  const decideRef = useRef<(decision: TriageDecision) => void>(() => { });

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
      if (live.current) setError(e instanceof CloudApiError ? e.message : 'Giornata non disponibile. Riprova.');
    } finally {
      if (live.current) setLoading(false);
    }
  }

  useEffect(() => {
    live.current = true;
    void load();
    return () => { live.current = false; };
  }, [api]);

  const range = deckWindow(now);
  const items = snapshot?.items ?? [];
  const pending = pendingCards(items, now, range.dayStart);
  const card = pending[0] ?? null;
  const counts = decisionCounts(items);
  const troubled = (snapshot?.accounts ?? []).filter(a => a.status !== 'ok');
  const canApply = card !== null && canApplyToSource(card.item, providers);

  async function decide(decision: TriageDecision) {
    if (!card || busy) return;
    setBusy(true);
    setError('');
    const apply = decision === 'done' && canApply && applyToSource;
    try {
      const result = await api.decide(card.key, decision,
        { applyToSource: apply, snoozeMinutes: decision === 'later' ? 120 : undefined });
      if (!live.current) return;
      const decidedAt = new Date();
      setSnapshot(current => current === null ? current : {
        ...current,
        items: withDecision(current.items, result.key, result.decision, result.snoozedUntil,
          result.appliedToSource, decidedAt)
      });
      setNow(decidedAt);
      setApplyToSource(false);
    } catch (e) {
      if (live.current) setError(e instanceof CloudApiError ? e.message : 'Decisione non salvata. Riprova.');
    } finally {
      if (live.current) {
        setBusy(false);
        Animated.spring(position, { toValue: 0, useNativeDriver: false }).start();
      }
    }
  }
  decideRef.current = (decision) => { void decide(decision); };

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
    <View testID="today-panel" style={[styles.card, { backgroundColor: p.surface, borderColor: p.border }]}>
      <View style={styles.header}>
        <View style={{ flex: 1 }}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{demo ? 'OGGI · DEMO CON DATI DI ESEMPIO' : 'OGGI'}</Text>
          <Text style={[styles.title, heading]}>
            {card ? 'Cosa ne facciamo?' : snapshot && snapshot.accounts.length > 0 ? 'Giornata in ordine' : 'La tua giornata'}
          </Text>
        </View>
        <Pressable testID="today-reload" accessibilityRole="button" onPress={() => { void load(); }}
          disabled={loading || busy} style={[styles.smallButton, { backgroundColor: p.card2, opacity: loading ? 0.5 : 1 }]}>
          <Text style={[styles.smallText, heading]}>{loading ? 'Carico…' : 'Aggiorna'}</Text>
        </Pressable>
      </View>

      {troubled.map(account => (
        <View key={account.accountId} testID="today-account-warning" style={[styles.warning, { borderColor: p.border }]}>
          <Text style={[styles.hint, heading, { marginTop: 0 }]}>
            {account.displayName}: {account.status === 'reconnect_required'
              ? 'accesso scaduto, ricollega l’account nelle connessioni.' : 'provider non raggiungibile, dati parziali.'}
          </Text>
        </View>
      ))}

      {snapshot && snapshot.accounts.length === 0 && (
        <Text testID="today-empty-accounts" style={[styles.hint, { color: p.subtle }]}>
          Collega Microsoft 365 o Google Workspace qui sotto: riunioni, email da leggere, attività e chat
          arriveranno qui come carte da smistare.
        </Text>
      )}

      {card && (
        <Animated.View testID="today-card" {...pan.panHandlers}
          style={[styles.deckCard, { backgroundColor: p.bg, borderColor: p.border,
            transform: [{ translateX: position }, { rotate: position.interpolate({
              inputRange: [-300, 0, 300], outputRange: ['-6deg', '0deg', '6deg'] }) }] }]}>
          <Text style={[styles.kind, { color: p.accent }]}>
            {kindIcons[card.item.kind]} {kindLabels[card.item.kind].toUpperCase()}
            {card.item.important ? ' · IMPORTANTE' : ''}{card.item.unread ? ' · NON LETTA' : ''}
          </Text>
          <Text testID="today-card-title" style={[styles.cardTitle, heading]}>{card.item.title}</Text>
          <Text style={[styles.hint, { color: p.subtle }]}>
            {whenLabel(card.item, now)}{card.item.counterpart ? ' · ' + card.item.counterpart : ''}
          </Text>
          {!!card.item.detail && <Text style={[styles.detail, heading]}>{card.item.detail}</Text>}
          {!!suggestions[card.key] && (
            <View testID="today-pre-action" style={[styles.preAction, { borderColor: p.accent }]}>
              <Text style={[styles.kind, { color: p.accent }]}>PRE-AZIONE SUGGERITA · AI</Text>
              <Text style={[styles.detail, heading, { marginTop: 4 }]}>{suggestions[card.key]}</Text>
            </View>
          )}
          {link && (
            <Pressable accessibilityRole="link" onPress={() => { void Linking.openURL(link); }}>
              <Text style={[styles.hint, { color: p.accent }]}>Apri nell’app di origine ↗</Text>
            </Pressable>
          )}
          {canApply && (
            <Pressable testID="today-apply-source" accessibilityRole="checkbox"
              aria-checked={applyToSource} onPress={() => setApplyToSource(v => !v)}
              style={styles.checkRow}>
              <Text style={[styles.check, { borderColor: p.text, color: p.text }]}>{applyToSource ? '✓' : ' '}</Text>
              <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>
                Con “Fatto”, segna completata anche su {providerTaskApp[card.item.provider] ?? card.item.provider}
                {' '}(azione registrata nel tuo audit).
              </Text>
            </Pressable>
          )}
          <View style={styles.actions}>
            <Action testID="today-not-done" label="✗ Non fatto" onPress={() => { void decide('not_done'); }} p={p} disabled={busy} />
            <Action testID="today-later" label="⏱ Dopo" onPress={() => { void decide('later'); }} p={p} disabled={busy} />
            <Action testID="today-dismiss" label="Scarta" onPress={() => { void decide('dismissed'); }} p={p} disabled={busy} />
            <Action testID="today-done" label="✓ Fatto" onPress={() => { void decide('done'); }} p={p} disabled={busy} primary />
          </View>
          <Text style={[styles.hint, { color: p.subtle }]}>
            {Platform.OS === 'web' ? 'Trascina la carta: destra = fatto, sinistra = non fatto. ' : ''}
            In coda: {pending.length}.
          </Text>
        </Animated.View>
      )}

      {snapshot && !card && snapshot.accounts.length > 0 && (
        <Text testID="today-clear" style={[styles.hint, heading]}>Nessuna carta in sospeso. Ottimo lavoro.</Text>
      )}
      {snapshot && items.length > 0 && (
        <Text testID="today-summary" style={[styles.hint, { color: p.subtle }]}>
          Fatte {counts.done} · Non fatte {counts.not_done} · Rimandate {counts.later} · Scartate {counts.dismissed}
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
  action: { minHeight: 44, paddingHorizontal: 14, borderRadius: 12, alignItems: 'center', justifyContent: 'center', flexGrow: 1 }
});
