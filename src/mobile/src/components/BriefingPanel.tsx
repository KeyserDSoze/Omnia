import { useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import {
  ASSISTANT_PURPOSE, ASSISTANT_SOURCE, CloudApiError, type AssistantInfoDto, type BriefingDto, type MemoryApi
} from '../services/memoryApi';
import { deckWindow } from '../services/todayDeck';
import { getLocale, useT } from '../i18n';
import type { Palette } from './TodayPanel';

/**
 * "Piano del giorno" (AI, opt-in). Separate consent from note capture; the disclosure states
 * exactly what leaves Tao. The output is labeled as AI, kept in memory only, and never acted on.
 */
export function BriefingPanel({ api, p, onSuggestions }: {
  api: MemoryApi;
  p: Palette;
  onSuggestions: (suggestions: Record<string, string>) => void;
}) {
  const t = useT();
  const [info, setInfo] = useState<AssistantInfoDto | null>(null);
  const [consented, setConsented] = useState(false);
  const [acknowledged, setAcknowledged] = useState(false);
  const [briefing, setBriefing] = useState<BriefingDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const live = useRef(true);

  useEffect(() => {
    live.current = true;
    void (async () => {
      try {
        const assistant = await api.assistant();
        if (!live.current) return;
        setInfo(assistant);
        if (!assistant.configured) return;
        const grants = await api.listConsents();
        if (live.current)
          setConsented(grants.some(g => g.source === ASSISTANT_SOURCE && g.purpose === ASSISTANT_PURPOSE && g.active));
      } catch {
        if (live.current) setMessage(t('briefing.statusFailed'));
      }
    })();
    return () => { live.current = false; };
  }, [api]);

  async function activate() {
    if (busy || !acknowledged) return;
    setBusy(true);
    setMessage('');
    try {
      await api.grantAssistantBriefing();
      if (live.current) setConsented(true);
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('briefing.activateFailed'));
    } finally {
      if (live.current) setBusy(false);
    }
  }

  async function generate() {
    if (busy) return;
    setBusy(true);
    setMessage('');
    try {
      const range = deckWindow(new Date());
      const result = await api.briefing(range.from, range.to,
        Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', getLocale());
      if (!live.current) return;
      setBriefing(result);
      onSuggestions(Object.fromEntries(result.suggestions.map(s => [s.key, s.preAction])));
    } catch (e) {
      if (!live.current) return;
      if (e instanceof CloudApiError && e.code === 'assistant_consent_required') setConsented(false);
      setMessage(e instanceof CloudApiError ? e.message : t('briefing.failed'));
    } finally {
      if (live.current) setBusy(false);
    }
  }

  if (!info) return null;
  const heading = { color: p.text };
  return (
    <View testID="briefing-panel" style={[styles.card, { backgroundColor: p.surface, borderColor: p.border }]}>
      <Text style={[styles.kicker, { color: p.subtle }]}>{t('briefing.kicker')}</Text>
      {!info.configured ? (
        <Text testID="briefing-unconfigured" style={[styles.hint, { color: p.subtle }]}>{t('briefing.unconfigured')}</Text>
      ) : !consented ? (
        <>
          <Text style={[styles.hint, heading]}>
            {t('briefing.disclosure', { model: info.model ?? '', host: info.host ?? '' })}
          </Text>
          <Pressable testID="briefing-acknowledge" accessibilityRole="checkbox" aria-checked={acknowledged}
            onPress={() => setAcknowledged(v => !v)} style={styles.checkRow}>
            <Text style={[styles.check, { borderColor: p.text, color: p.text }]}>{acknowledged ? '✓' : ' '}</Text>
            <Text style={[styles.hint, heading, { flex: 1, marginTop: 0 }]}>{t('briefing.ack')}</Text>
          </Pressable>
          <Pressable testID="briefing-activate" accessibilityRole="button" disabled={!acknowledged || busy}
            onPress={() => { void activate(); }}
            style={[styles.button, { backgroundColor: p.card2, opacity: acknowledged && !busy ? 1 : 0.5 }]}>
            <Text style={[styles.buttonText, heading]}>{t('briefing.activate')}</Text>
          </Pressable>
        </>
      ) : (
        <>
          <Pressable testID="briefing-generate" accessibilityRole="button" disabled={busy}
            onPress={() => { void generate(); }}
            style={[styles.button, { backgroundColor: p.accent, opacity: busy ? 0.5 : 1 }]}>
            <Text style={[styles.buttonText, { color: p.accentText }]}>
              {busy ? t('briefing.preparing') : briefing ? t('briefing.regenerate') : t('briefing.generate')}
            </Text>
          </Pressable>
          {briefing && (
            <View testID="briefing-result">
              {briefing.cardsSent === 0 ? (
                <Text style={[styles.summary, heading]}>{t('briefing.nothingOpen')}</Text>
              ) : (
                <>
                  <Text style={[styles.summary, heading]}>{briefing.summary}</Text>
                  <Text style={[styles.hint, { color: p.subtle }]}>
                    {t('briefing.generatedBy', { model: briefing.model, count: briefing.cardsSent })}
                    {briefing.suggestions.length > 0 ? ' ' + t('briefing.onCards') : ''}
                  </Text>
                </>
              )}
            </View>
          )}
        </>
      )}
      {!!message && <Text accessibilityRole="alert" testID="briefing-message" style={[styles.hint, heading]}>{message}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 25, padding: 26, marginBottom: 20 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.2 },
  hint: { fontSize: 12, lineHeight: 18, marginTop: 8 },
  summary: { fontSize: 16, lineHeight: 24, marginTop: 14, fontWeight: '600' },
  checkRow: { flexDirection: 'row', alignItems: 'center', gap: 10, marginTop: 12 },
  check: { width: 22, height: 22, borderWidth: 1.5, borderRadius: 6, textAlign: 'center', fontWeight: '800', lineHeight: 19 },
  button: { minHeight: 44, paddingHorizontal: 16, borderRadius: 12, alignItems: 'center', justifyContent: 'center',
    marginTop: 14, alignSelf: 'flex-start' },
  buttonText: { fontSize: 14, fontWeight: '700' }
});
