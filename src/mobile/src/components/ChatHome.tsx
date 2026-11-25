import { useEffect, useRef, useState } from 'react';
import { Platform, Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import {
  CloudApiError, type ConnectorProviderDto, type ConnectorProviderId, type MemoryApi
} from '../services/memoryApi';
import {
  answer, currentStep, startSetup, toSaveBody, type DraftAccount, type SettingsDto, type SetupState
} from '../services/setupFlow';
import { greeting, hints, intentOf, noteText, openingLines, type ChatAction, type DayRecapDto } from '../services/chatScript';
import { connectInPopup, openConnectPopup } from '../services/connectFlow';
import type { TodaySource } from '../services/demoToday';
import { demoProviders } from '../services/demoToday';
import { TodayPanel, type Palette } from './TodayPanel';
import { BriefingPanel } from './BriefingPanel';

type Message =
  | { id: number; from: 'tao' | 'user'; text: string }
  | { id: number; from: 'tao'; widget: 'today' | 'briefing' };

export type ChatMode = 'demo' | 'checking' | 'signed-out' | 'signed-in' | 'invalid';

const providerLabel: Record<ConnectorProviderId, string> = { microsoft: 'Microsoft', google: 'Google' };

/**
 * Home screen: a chat. Tao opens with a question or hints from previous days; the magic button
 * starts the scripted setup (no AI) that composes the settings saved on the server.
 */
export function ChatHome({ p, mode, invalidReason, api, demo, profileName, socialProviders, onLogin,
  onOpenMemory, onOpenSettings, setupRequest }: {
  p: Palette;
  mode: ChatMode;
  invalidReason?: string;
  api: MemoryApi | null;
  demo: TodaySource | null;
  profileName: string | null;
  socialProviders: ConnectorProviderId[];
  onLogin: (idp?: ConnectorProviderId) => void;
  onOpenMemory: (prefill?: string) => void;
  onOpenSettings: () => void;
  /** Incremented by the settings screen to re-run the setup from the chat. */
  setupRequest: number;
}) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [settings, setSettings] = useState<SettingsDto | null>(null);
  const [providers, setProviders] = useState<ConnectorProviderDto[]>(demo ? demoProviders : []);
  const [assistant, setAssistant] = useState(false);
  const [setup, setSetup] = useState<SetupState | null>(null);
  const [picks, setPicks] = useState<string[]>([]);
  const [queue, setQueue] = useState<DraftAccount[] | null>(null);
  const [input, setInput] = useState('');
  const [busy, setBusy] = useState(false);
  const [preActions, setPreActions] = useState<Record<string, string>>({});
  const nextId = useRef(0);
  const live = useRef(true);

  const say = (text: string, from: 'tao' | 'user' = 'tao') =>
    setMessages(list => [...list, { id: nextId.current++, from, text }]);
  const show = (widget: 'today' | 'briefing') =>
    setMessages(list => [...list.filter(m => !('widget' in m) || m.widget !== widget),
      { id: nextId.current++, from: 'tao', widget }]);

  useEffect(() => {
    live.current = true;
    setMessages([]);
    setSetup(null);
    setQueue(null);
    if (mode === 'demo') {
      say('Ciao! Questa è la demo di Tao: nessun dato reale, niente viene salvato.');
      say('Prova a smistare una giornata di esempio, oppure fai la configurazione guidata.');
    } else if (mode === 'signed-out') {
      say('Ciao! Sono Tao, il tuo assistente di lavoro. Accedi per iniziare.');
    } else if (mode === 'signed-in' && api) {
      void (async () => {
        try {
          const state = await api.settings();
          // Before the setup there is no day to recap and nothing to plan: one request only.
          let recap: DayRecapDto[] = [];
          let configured = false;
          if (state.settings?.onboardingCompletedAt) {
            const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
            const [past, info] = await Promise.all([
              api.recap(3, zone).catch(() => ({ days: [] })),
              api.assistant().catch(() => ({ configured: false, model: null, host: null }))
            ]);
            recap = past.days;
            configured = info.configured;
          }
          if (!live.current) return;
          setSettings(state.settings);
          setProviders(state.providers);
          setAssistant(configured);
          say(greeting(profileName, state.settings, new Date()));
          for (const line of openingLines(state.settings, recap)) say(line);
        } catch (e) {
          if (live.current) say(e instanceof CloudApiError ? e.message : 'Non riesco a caricare le tue impostazioni.');
        }
      })();
    }
    return () => { live.current = false; };
  }, [mode, api]);

  useEffect(() => {
    if (setupRequest > 0) beginSetup();
  }, [setupRequest]);

  function beginSetup() {
    const state = startSetup(settings?.displayName ?? profileName);
    setSetup(state);
    setPicks([]);
    setQueue(null);
    say(currentStep(state).question);
  }

  function reply(value: string | string[], shown: string) {
    if (!setup) return;
    let next: SetupState;
    try { next = answer(setup, value); }
    catch { say('Scegli una delle risposte proposte.'); return; }
    say(shown, 'user');
    setPicks([]);
    if (next.step === 'done') {
      setSetup(null);
      void save(next.draft);
      return;
    }
    setSetup(next);
    say(currentStep(next).question);
  }

  async function save(draft: SettingsDto) {
    if (!api) {
      setSettings({ ...draft, onboardingCompletedAt: new Date().toISOString() });
      say('Fatto! In demo non salvo nulla, ma ecco come funzionerebbe: adesso ti chiederei di collegare gli account.');
      return;
    }
    setBusy(true);
    try {
      const saved = await api.saveSettings(toSaveBody(draft, true));
      if (!live.current) return;
      setSettings(saved);
      say('Configurazione salvata: Tao sa quali account hai e cosa può fare per te. Puoi cambiarla dalle impostazioni in alto a destra.');
      startConnectQueue(saved);
    } catch (e) {
      if (live.current) say(e instanceof CloudApiError ? e.message : 'Salvataggio non riuscito. Riprova.');
    } finally {
      if (live.current) setBusy(false);
    }
  }

  function startConnectQueue(current: SettingsDto) {
    const pending = current.accounts.filter(a => !a.connectedAccountId);
    const usable = pending.filter(a => providers.some(pr => pr.id === a.provider && pr.configured));
    if (pending.length > 0 && usable.length === 0) {
      say('Per collegare gli account serve che l’amministratore configuri le app Microsoft/Google sul server (vedi docs/connectors.md). Le tue risposte restano salvate.');
      return;
    }
    if (usable.length === 0) { say('Tutto pronto. Da dove partiamo?'); return; }
    setQueue(usable);
    say(`Colleghiamo «${usable[0].label}» (${providerLabel[usable[0].provider]}) adesso? Si apre la pagina di accesso ufficiale.`);
  }

  async function connectFirst() {
    if (!api || !queue?.length || !settings || Platform.OS !== 'web') return;
    const target = queue[0];
    const popup = openConnectPopup();
    if (!popup) { say('Il browser ha bloccato la finestra: consenti i popup per questo sito e riprova.'); return; }
    say('Collega ora', 'user');
    setBusy(true);
    try {
      const account = await connectInPopup(api, target.provider, popup, new AbortController().signal);
      if (!live.current) return;
      if (!account) { say('Accesso annullato. Possiamo riprovare quando vuoi.'); return; }
      const linked = await api.saveSettings(toSaveBody({
        ...settings,
        accounts: settings.accounts.map(a => a.id === target.id ? { ...a, connectedAccountId: account.id } : a)
      }, false));
      if (!live.current) return;
      setSettings(linked);
      say(`«${target.label}» collegato come ${account.displayName}.`);
      nextInQueue();
    } catch (e) {
      if (live.current) say(e instanceof CloudApiError ? e.message : 'Collegamento non riuscito. Riprova.');
    } finally {
      if (live.current) setBusy(false);
    }
  }

  function nextInQueue(skipped = false) {
    if (skipped) say('Più tardi', 'user');
    const rest = (queue ?? []).slice(1);
    setQueue(rest.length > 0 ? rest : null);
    if (rest.length === 0)
      say('Perfetto. Se hai altri account associati puoi aggiungerli quando vuoi con «Configura Tao». Intanto, da dove partiamo?');
    else
      say(`E «${rest[0].label}» (${providerLabel[rest[0].provider]})? Lo colleghiamo adesso?`);
  }

  function act(action: ChatAction, label?: string) {
    if (label) say(label, 'user');
    switch (action) {
      case 'today': say('Ecco le carte di oggi: una alla volta.'); show('today'); break;
      case 'briefing':
        if (!api) { say('Il piano del giorno usa un modello AI e funziona sul server, non nella demo.'); break; }
        show('briefing'); break;
      case 'setup':
        if (settings?.onboardingCompletedAt && settings.accounts.some(a => !a.connectedAccountId) && api)
          startConnectQueue(settings);
        else beginSetup();
        break;
      case 'note': onOpenMemory(''); break;
      case 'memory': onOpenMemory(); break;
      case 'settings': onOpenSettings(); break;
      case 'unknown':
        say('Non ho ancora un modello per le domande libere: posso smistare la giornata, salvare una nota o configurare i tuoi account.');
        break;
    }
  }

  function submit() {
    const text = input.trim();
    if (!text) return;
    setInput('');
    if (setup && currentStep(setup).input === 'text') { reply(text, text); return; }
    say(text, 'user');
    const intent = intentOf(text);
    if (intent === 'note') { onOpenMemory(noteText(text)); return; }
    act(intent);
  }

  const heading = { color: p.text };
  const step = setup ? currentStep(setup) : null;
  const hintList = mode === 'demo'
    ? [{ id: 'today' as const, label: 'Smista la giornata (demo)' }, { id: 'setup' as const, label: '✨ Configura Tao' },
      { id: 'memory' as const, label: 'Le mie note' }]
    : mode === 'signed-in' ? hints(settings, assistant) : [];

  return (
    <View testID="chat-home" style={styles.wrap}>
      {mode === 'checking' && <Text style={[styles.hint, { color: p.subtle }]}>Verifica della sessione…</Text>}
      {mode === 'invalid' && <Text testID="chat-invalid" style={[styles.hint, heading]}>{invalidReason}</Text>}
      {messages.map(m => 'widget' in m ? (
        <View key={m.id} style={styles.widget}>
          {m.widget === 'today' && (api || demo) && (
            <TodayPanel api={api ?? demo!} providers={providers} p={p} demo={!api} suggestions={preActions} />
          )}
          {m.widget === 'briefing' && api && <BriefingPanel api={api} p={p} onSuggestions={setPreActions} />}
        </View>
      ) : (
        <View key={m.id} testID={m.from === 'tao' ? 'chat-tao' : 'chat-user'}
          style={[styles.bubble, m.from === 'tao'
            ? { backgroundColor: p.surface, borderColor: p.border, alignSelf: 'flex-start' }
            : { backgroundColor: p.accent, borderColor: p.accent, alignSelf: 'flex-end' }]}>
          <Text style={[styles.text, { color: m.from === 'tao' ? p.text : p.accentText }]}>{m.text}</Text>
        </View>
      ))}

      {mode === 'signed-out' && (
        <View style={styles.row}>
          <Chip testID="chat-login" label="Accedi" onPress={() => onLogin()} p={p} primary />
          {socialProviders.map(id => (
            <Chip key={id} testID={'chat-login-' + id} label={'Accedi con ' + providerLabel[id]}
              onPress={() => onLogin(id)} p={p} />
          ))}
        </View>
      )}

      {step && step.input !== 'none' && (
        <View testID="setup-step" style={styles.row}>
          {step.choices.map(choice => {
            const selected = picks.includes(choice.id);
            return <Chip key={choice.id} testID={'setup-choice-' + choice.id} label={(selected ? '✓ ' : '') + choice.label} p={p}
              selected={selected}
              onPress={() => step.input === 'multi'
                ? setPicks(list => selected ? list.filter(x => x !== choice.id) : [...list, choice.id])
                : reply(choice.id, choice.label)} />;
          })}
          {step.input === 'multi' && (
            <Chip testID="setup-confirm" label="Conferma" p={p} primary disabled={picks.length === 0}
              onPress={() => reply(picks, step.choices.filter(c => picks.includes(c.id)).map(c => c.label).join(', '))} />
          )}
        </View>
      )}

      {queue && queue.length > 0 && !busy && (
        <View style={styles.row}>
          <Chip testID="connect-now" label="Collega ora" p={p} primary onPress={() => { void connectFirst(); }} />
          <Chip testID="connect-later" label="Più tardi" p={p} onPress={() => nextInQueue(true)} />
        </View>
      )}

      {!setup && !queue && hintList.length > 0 && (
        <View style={styles.row}>
          {hintList.map(h => (
            <Chip key={h.id} testID={h.id === 'setup' && (mode === 'demo' || !settings?.onboardingCompletedAt) ? 'setup-start' : 'chat-hint-' + h.id}
              label={h.label} p={p} primary={h.id === 'setup' && !settings?.onboardingCompletedAt}
              onPress={() => act(h.id, h.label)} />
          ))}
        </View>
      )}

      {(mode === 'signed-in' || mode === 'demo') && (
        <View style={[styles.inputRow, { borderColor: p.border, backgroundColor: p.surface }]}>
          <TextInput testID="chat-input" value={input} onChangeText={setInput} onSubmitEditing={submit}
            placeholder={step?.input === 'text' ? step.placeholder : 'Scrivi a Tao… (es. «cosa devo fare oggi?»)'}
            placeholderTextColor={p.subtle} style={[styles.input, { color: p.text }]} editable={!busy} />
          <Pressable testID="chat-send" accessibilityRole="button" onPress={submit} disabled={busy}
            style={[styles.send, { backgroundColor: p.accent, opacity: busy ? 0.5 : 1 }]}>
            <Text style={{ color: p.accentText, fontWeight: '800' }}>↑</Text>
          </Pressable>
        </View>
      )}
    </View>
  );
}

function Chip({ label, onPress, p, primary, selected, disabled, testID }: {
  label: string; onPress: () => void; p: Palette; primary?: boolean; selected?: boolean; disabled?: boolean;
  testID?: string;
}) {
  return (
    <Pressable testID={testID} accessibilityRole="button" onPress={onPress} disabled={disabled}
      style={[styles.chip, {
        backgroundColor: primary ? p.accent : selected ? p.card2 : p.surface,
        borderColor: primary || selected ? p.accent : p.border, opacity: disabled ? 0.5 : 1
      }]}>
      <Text style={[styles.chipText, { color: primary ? p.accentText : p.text }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  wrap: { gap: 10, paddingTop: 28, paddingBottom: 12, maxWidth: 760, width: '100%', alignSelf: 'center' },
  bubble: { borderWidth: 1, borderRadius: 18, paddingHorizontal: 16, paddingVertical: 11, maxWidth: '88%' },
  text: { fontSize: 15, lineHeight: 22 },
  widget: { alignSelf: 'stretch' },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 4 },
  chip: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 9 },
  chipText: { fontSize: 14, fontWeight: '700' },
  hint: { fontSize: 13, lineHeight: 19 },
  inputRow: { flexDirection: 'row', alignItems: 'center', borderWidth: 1, borderRadius: 18, paddingLeft: 14,
    paddingRight: 6, paddingVertical: 6, marginTop: 14, gap: 8 },
  input: { flex: 1, fontSize: 15, minHeight: 36 },
  send: { width: 38, height: 38, borderRadius: 19, alignItems: 'center', justifyContent: 'center' }
});
