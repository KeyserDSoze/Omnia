import { useState } from 'react';
import { useColorScheme, Pressable, ScrollView, Text, TextInput, View, StyleSheet } from 'react-native';
import { RecordingPresets, requestRecordingPermissionsAsync, setAudioModeAsync, useAudioRecorder } from 'expo-audio';
import { normalizeMemo } from '../domain/tags';

type LocalMemo = { id: string; content: string; tags: string[]; time: string };
type Palette = { bg: string; surface: string; text: string; subtle: string; border: string; accent: string; accentText: string; card2: string };
const palettes: Record<'dark' | 'light', Palette> = {
  dark: { bg: '#0D1421', surface: '#172234', text: '#F5F8FF', subtle: '#9BA9BB', border: '#29374A', accent: '#A5E6D4', accentText: '#092D2A', card2: '#203246' },
  light: { bg: '#F5F8FA', surface: '#FFFFFF', text: '#152537', subtle: '#53657B', border: '#DFE7EB', accent: '#176F68', accentText: '#FFFFFF', card2: '#E9F3F1' }
};

export default function Home() {
  const device = useColorScheme();
  const [override, setOverride] = useState<'light' | 'dark' | null>(null);
  const mode = override ?? (device === 'dark' ? 'dark' : 'light');
  const p = palettes[mode];
  const [draft, setDraft] = useState('');
  const [memos, setMemos] = useState<LocalMemo[]>([]);
  const [recording, setRecording] = useState(false);
  const [audioUri, setAudioUri] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const recorder = useAudioRecorder(RecordingPresets.HIGH_QUALITY);

  function captureText() {
    try {
      const occurredAt = new Date().toISOString();
      const normalized = normalizeMemo(draft, occurredAt);
      setMemos(current => [{ id: occurredAt, content: normalized.content, tags: normalized.tags,
        time: new Date().toLocaleTimeString('it-IT', { hour: '2-digit', minute: '2-digit' }) }, ...current]);
      setDraft('');
      setNotice('Memo aggiunto a questa sessione. La sincronizzazione richiederà un accesso sicuro.');
    } catch {
      setNotice('Scrivi qualcosa prima di salvare il memo.');
    }
  }

  async function captureAudio() {
    try {
      if (recording) {
        await recorder.stop();
        setRecording(false);
        setAudioUri(recorder.uri ?? null);
        setNotice('Registrazione completata sul dispositivo. Nessun audio inviato al server.');
        return;
      }
      const { granted } = await requestRecordingPermissionsAsync();
      if (!granted) { setNotice('Permesso microfono non concesso.'); return; }
      await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
      await recorder.prepareToRecordAsync();
      recorder.record();
      setAudioUri(null);
      setRecording(true);
      setNotice('Registrazione microfono in corso. Premi di nuovo per fermare.');
    } catch {
      setRecording(false);
      setNotice('Registrazione non disponibile su questo dispositivo/browser.');
    }
  }

  const cardStyle = { backgroundColor: p.surface, borderColor: p.border };
  const heading = { color: p.text };
  return (
    <ScrollView style={{ flex: 1, backgroundColor: p.bg }} contentContainerStyle={styles.page}>
      <View style={styles.top}>
        <View style={styles.brand}>
          <View style={[styles.mark, { backgroundColor: p.accent }]}><Text style={{ color: p.accentText, fontWeight: '900', fontSize: 19 }}>O</Text></View>
          <View><Text style={[styles.brandName, heading]}>omnia</Text><Text style={[styles.eyebrow, { color: p.subtle }]}>PERSONAL INTELLIGENCE</Text></View>
        </View>
        <Pressable testID="theme-toggle" accessibilityRole="button" accessibilityLabel="Cambia tema" onPress={() => setOverride(mode === 'dark' ? 'light' : 'dark')} style={[styles.themeButton, { borderColor: p.border, backgroundColor: p.surface }]}>
          <Text style={{ color: p.text }}>{mode === 'dark' ? '☀ Tema chiaro' : '☾ Tema scuro'}</Text>
        </Pressable>
      </View>
      <View style={styles.hero}>
        <Text style={[styles.eyebrow, { color: p.accent }]}>IL TUO SPAZIO PERSONALE</Text>
        <Text style={[styles.title, heading]}>La tua giornata.{"\n"}Più chiara, ogni giorno.</Text>
        <Text style={[styles.description, { color: p.subtle }]}>Un posto per raccogliere i tuoi momenti, collegare i puntini e costruire una memoria che controlli tu.</Text>
      </View>
      <View style={styles.columns}>
        <View style={[styles.card, cardStyle, styles.primaryCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>CATTURA UN MOMENTO</Text>
          <Text style={[styles.cardTitle, heading]}>Cosa vuoi ricordare?</Text>
          <TextInput testID="memo-input" placeholder="Una riunione, un pensiero, un'intuizione…" placeholderTextColor={p.subtle} multiline value={draft} onChangeText={setDraft} style={[styles.input, { color: p.text, backgroundColor: p.bg, borderColor: p.border }]} />
          <Pressable testID="save-memo" onPress={captureText} accessibilityRole="button" style={[styles.mainButton, { backgroundColor: p.accent }]}>
            <Text style={[styles.buttonText, { color: p.accentText }]}>＋ Salva nota rapida</Text>
          </Pressable>
          <View style={[styles.separator, { backgroundColor: p.border }]} />
          <Pressable testID="record-audio" onPress={() => { void captureAudio(); }} accessibilityRole="button" style={[styles.secondaryButton, { backgroundColor: p.card2 }]}>
            <Text style={[styles.buttonText, heading]}>{recording ? '■ Ferma registrazione' : '◉ Registra memo vocale'}</Text>
          </Pressable>
          <Text style={[styles.hint, { color: p.subtle }]}>Registra il microfono con consenso esplicito. Le chiamate telefoniche non vengono intercettate.</Text>
          {audioUri && <Text testID="audio-result" style={[styles.hint, heading]}>Memo audio creato sul dispositivo.</Text>}
          {!!notice && <Text accessibilityRole="alert" testID="notice" style={[styles.notice, { color: p.text }]}>{notice}</Text>}
        </View>
        <View style={[styles.card, cardStyle, styles.sideCard]}>
          <Text style={[styles.kicker, { color: p.subtle }]}>LA TUA MEMORIA</Text>
          <Text style={[styles.cardTitle, heading]}>I tuoi momenti</Text>
          {memos.length === 0 ? (
            <View style={styles.empty}>
              <Text style={{ fontSize: 35 }}>✦</Text>
              <Text style={[styles.emptyTitle, heading]}>Si comincia da qui.</Text>
              <Text style={[styles.hint, { color: p.subtle }]}>Le note di questa sessione appariranno qui. Non sono ancora sincronizzate sul cloud.</Text>
            </View>
          ) : memos.map(memo => <View key={memo.id} style={[styles.memo, { borderColor: p.border }]}>
            <Text style={[styles.memoTime, { color: p.subtle }]}>{memo.time} · {memo.tags.join(', ')}</Text>
            <Text style={[styles.memoText, heading]}>{memo.content}</Text>
          </View>)}
        </View>
      </View>
      <View style={styles.features}>
        {[
          { icon: '◈', title: 'Connetti', detail: 'Le tue app, i tuoi device e i tuoi servizi', state: 'IN ROADMAP' },
          { icon: '✧', title: 'Comprendi', detail: 'Relazioni, thread e ricerca semantica', state: 'IN ROADMAP' },
          { icon: '◇', title: 'Agisci', detail: 'Suggerimenti e azioni con il tuo consenso', state: 'IN ROADMAP' }
        ].map(feature => <View key={feature.title} style={[styles.feature, { borderColor: p.border }]}>
          <Text style={{ color: p.accent, fontSize: 25 }}>{feature.icon}</Text>
          <View style={{ flex: 1 }}><Text style={[styles.featureTitle, heading]}>{feature.title}</Text><Text style={[styles.hint, { color: p.subtle }]}>{feature.detail}</Text></View>
          <Text style={[styles.featureState, { color: p.subtle }]}>{feature.state}</Text>
        </View>)}
      </View>
      <Text style={[styles.footer, { color: p.subtle }]}>OMNIA · FOUNDATION 0.1 · PRIVACY BY DESIGN · NESSUNA AUTOMAZIONE ATTIVA</Text>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  page: { paddingHorizontal: 22, paddingTop: 32, paddingBottom: 44, width: '100%', maxWidth: 1120, alignSelf: 'center' },
  top: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', flexWrap: 'wrap', gap: 16 },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 12 }, mark: { width: 43, height: 43, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  brandName: { fontSize: 27, fontWeight: '800', letterSpacing: -1.6 }, eyebrow: { fontSize: 10, letterSpacing: 2, fontWeight: '800' },
  themeButton: { borderWidth: 1, borderRadius: 13, paddingHorizontal: 14, paddingVertical: 10 },
  hero: { paddingTop: 64, paddingBottom: 36, maxWidth: 790 },
  title: { fontSize: 48, lineHeight: 57, letterSpacing: -2.6, fontWeight: '800', paddingTop: 12 },
  description: { fontSize: 17, lineHeight: 26, maxWidth: 610, paddingTop: 16 },
  columns: { flexDirection: 'row', gap: 18, flexWrap: 'wrap' },
  card: { borderWidth: 1, borderRadius: 25, padding: 26, minHeight: 340 },
  primaryCard: { flex: 1.2, minWidth: 285 }, sideCard: { flex: 1, minWidth: 285 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.2 },
  cardTitle: { fontSize: 25, fontWeight: '700', letterSpacing: -0.8, marginTop: 12, marginBottom: 18 },
  input: { minHeight: 96, borderWidth: 1, borderRadius: 14, padding: 13, textAlignVertical: 'top', fontSize: 15, lineHeight: 22 },
  mainButton: { marginTop: 12, minHeight: 48, alignItems: 'center', justifyContent: 'center', borderRadius: 13 },
  secondaryButton: { minHeight: 48, alignItems: 'center', justifyContent: 'center', borderRadius: 13 },
  buttonText: { fontSize: 15, fontWeight: '700' }, separator: { height: 1, marginVertical: 15 },
  hint: { fontSize: 12, lineHeight: 18, marginTop: 8 }, notice: { marginTop: 12, fontSize: 12, lineHeight: 18 },
  empty: { paddingTop: 17, alignItems: 'flex-start' }, emptyTitle: { fontSize: 18, fontWeight: '700', paddingTop: 12 },
  memo: { borderTopWidth: 1, paddingVertical: 12 }, memoTime: { fontSize: 11, fontWeight: '700' }, memoText: { fontSize: 15, marginTop: 7, lineHeight: 22 },
  features: { marginTop: 20, flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  feature: { flex: 1, minWidth: 250, borderWidth: 1, borderRadius: 16, padding: 18, flexDirection: 'row', alignItems: 'center', gap: 12 },
  featureTitle: { fontSize: 16, fontWeight: '700' }, featureState: { fontSize: 9, fontWeight: '800' },
  footer: { fontSize: 10, letterSpacing: 1.5, textAlign: 'center', marginTop: 44 }
});
