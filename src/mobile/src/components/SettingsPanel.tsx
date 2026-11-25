import { useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, SPEECH_PURPOSE, SPEECH_SOURCE, type ConnectorAccountDto, type ConsentDto, type MemoryApi,
  type SettingsResponseDto
} from '../services/memoryApi';
import { newId, summary, toSaveBody, type DraftAccount, type SettingsDto } from '../services/setupFlow';
import type { AiEngine } from '../services/aiRouter';
import { ConnectionsPanel } from './ConnectionsPanel';
import type { VoiceControl } from './ChatHome';
import { web, type Theme } from './theme';

const capabilityNames: Record<string, string> = {
  'calendar.read': 'Calendario', 'mail.read': 'Email', 'tasks.read': 'Attività', 'chats.read': 'Chat',
  'tasks.complete': 'Completare attività'
};
const engineName: Record<AiEngine, string> = { browser: 'nel browser', server: 'sul server Tao', off: 'spento' };

/**
 * Account and settings (top right). Everything shown here is what the agent may read or do on
 * behalf of the user, per account; changes are saved immediately to the server. Without an
 * account (demo) only appearance, voice and notes are available.
 */
export function SettingsPanel({ api, theme: p, profile, accountConsoleUrl, onLogout, onOpenMemory, onRunSetup,
  onToggleTheme, voice }: {
  api: MemoryApi | null;
  theme: Theme;
  profile: { name: string | null; email: string | null } | null;
  accountConsoleUrl: string | null;
  onLogout: (() => void) | null;
  onOpenMemory: () => void;
  onRunSetup: () => void;
  onToggleTheme: () => void;
  voice: VoiceControl;
}) {
  const [state, setState] = useState<SettingsResponseDto | null>(null);
  const [speech, setSpeech] = useState<ConsentDto | null>(null);
  const [message, setMessage] = useState('');
  const [saving, setSaving] = useState(false);
  const live = useRef(true);

  async function load() {
    if (!api) return;
    try {
      const [result, grants] = await Promise.all([api.settings(), api.listConsents()]);
      if (!live.current) return;
      setState(result);
      setSpeech(grants.find(g => g.source === SPEECH_SOURCE && g.purpose === SPEECH_PURPOSE && g.active) ?? null);
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : 'Impostazioni non disponibili.');
    }
  }

  useEffect(() => {
    live.current = true;
    void load();
    return () => { live.current = false; };
  }, [api]);

  async function save(next: SettingsDto, done: string) {
    if (!api) return;
    setSaving(true);
    setMessage('');
    try {
      const saved = await api.saveSettings(toSaveBody(next, false));
      if (!live.current) return;
      setState(current => current && { ...current, settings: saved });
      setMessage(done);
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : 'Salvataggio non riuscito.');
    } finally {
      if (live.current) setSaving(false);
    }
  }

  async function toggleVoice() {
    const tts = voice.router.textToSpeech;
    if (!tts) return;
    if (voice.on) { voice.setOn(false); setMessage('Tao non legge più i suoi messaggi.'); return; }
    const kind = await tts.availability();
    if (!live.current) return;
    if (kind === 'none') { setMessage('Su questo dispositivo non c’è una voce italiana: aggiungila dalle impostazioni del sistema.'); return; }
    if (kind === 'online' && !speech) {
      setMessage('Qui c’è solo la voce online del browser: attivala toccando il simbolo nella chat, con il consenso ai servizi vocali.');
      return;
    }
    await tts.ready(kind === 'online');
    voice.setOn(true);
    if (live.current) setMessage(kind === 'local' ? 'Tao legge i suoi messaggi con una voce del dispositivo.'
      : 'Tao legge i suoi messaggi con la voce online del browser.');
  }

  async function revokeSpeech() {
    if (!api || !speech) return;
    setSaving(true);
    try {
      await api.revokeConsent(speech.id);
      if (!live.current) return;
      setSpeech(null);
      if (await voice.router.textToSpeech?.availability() === 'online') voice.setOn(false);
      setMessage('Servizi vocali del browser revocati: niente dettatura né voce online finché non li riattivi.');
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : 'Revoca non confermata. Riprova.');
    } finally {
      if (live.current) setSaving(false);
    }
  }

  const settings = state?.settings ?? null;
  const supported = (provider: string) =>
    state?.providers.find(x => x.id === provider)?.capabilities ?? [];

  function toggle(account: DraftAccount, capability: string) {
    if (!settings) return;
    const on = account.delegations.includes(capability);
    void save({
      ...settings,
      accounts: settings.accounts.map(a => a.id !== account.id ? a : {
        ...a, delegations: on ? a.delegations.filter(d => d !== capability) : [...a.delegations, capability]
      })
    }, `${account.label}: ${capabilityNames[capability] ?? capability} ${on ? 'disattivato' : 'attivato'}.`);
  }

  function linkConnected(account: ConnectorAccountDto) {
    if (!settings) return;
    if (settings.accounts.some(a => a.connectedAccountId === account.id)) return;
    const free = settings.accounts.find(a => a.provider === account.provider && !a.connectedAccountId);
    const accounts = free
      ? settings.accounts.map(a => a.id === free.id ? { ...a, connectedAccountId: account.id } : a)
      : [...settings.accounts, {
          id: newId(), provider: account.provider, usage: 'work' as const, label: account.displayName.slice(0, 60),
          connectedAccountId: account.id,
          delegations: supported(account.provider).filter(c => c.endsWith('.read') ||
            (settings.agentMode === 'act_with_confirmation' && c === 'tasks.complete'))
        }];
    void save({ ...settings, accounts }, `${account.displayName} aggiunto alla configurazione.`);
  }

  const heading = { color: p.text };
  const card = [styles.card, { backgroundColor: p.surface, borderColor: p.border },
    web({ backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)' })];
  const initials = (profile?.name ?? profile?.email ?? '?').split(/[\s@.]+/).filter(Boolean).slice(0, 2)
    .map(x => x[0]!.toUpperCase()).join('');
  const connectedIds = new Set(state?.accounts.map(a => a.id) ?? []);
  const routes = voice.router.routes;

  return (
    <View testID="settings-panel" style={styles.wrap}>
      {api && profile ? (
        <View style={card}>
          <View style={styles.accountRow}>
            <View style={[styles.avatar, { backgroundColor: p.accent }]}>
              <Text style={{ color: p.accentText, fontWeight: '800', fontSize: 18 }}>{initials}</Text>
            </View>
            <View style={{ flex: 1 }}>
              <Text testID="settings-name" style={[styles.title, heading]}>{profile.name ?? 'Il tuo account'}</Text>
              {!!profile.email && <Text style={[styles.hint, { color: p.subtle, marginTop: 2 }]}>{profile.email}</Text>}
            </View>
          </View>
          <View style={styles.row}>
            {accountConsoleUrl && (
              <Button testID="settings-account-console" label="Accesso e social login (Google, Microsoft)" p={p}
                onPress={() => { if (typeof window !== 'undefined') window.open(accountConsoleUrl, '_blank', 'noopener'); }} />
            )}
            {onLogout && <Button testID="settings-logout" label="Esci" p={p} onPress={onLogout} />}
          </View>
          <Text style={[styles.hint, { color: p.subtle }]}>
            Puoi accedere a Tao anche con Google o Microsoft se l’amministratore li ha attivati: collega l’identità dalla
            pagina «Accesso e social login», nella sezione account collegati.
          </Text>
        </View>
      ) : (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>DEMO</Text>
          <Text style={[styles.hint, heading]}>Nessun account: qui provi aspetto, voce e note. Niente viene salvato.</Text>
        </View>
      )}

      <View style={card}>
        <Text style={[styles.kicker, { color: p.subtle }]}>ASPETTO E VOCE</Text>
        <View style={styles.row}>
          <Button testID="theme-toggle" p={p} onPress={onToggleTheme}
            label={p.mode === 'dark' ? '☀ Tema chiaro' : '☾ Tema scuro'} />
          {voice.router.textToSpeech && (
            <Button testID="settings-voice" p={p} selected={voice.on} onPress={() => { void toggleVoice(); }}
              label={voice.on ? '✓ Tao parla ad alta voce' : 'Fai parlare Tao'} />
          )}
          {api && speech && (
            <Button testID="settings-speech-revoke" p={p} disabled={saving} onPress={() => { void revokeSpeech(); }}
              label="Revoca i servizi vocali" />
          )}
        </View>
        <Text testID="settings-speech" style={[styles.hint, heading]}>
          {!voice.router.speechToText && !voice.router.textToSpeech
            ? 'Voce non disponibile in questo browser o spenta sul server.'
            : !api ? 'Servizi vocali del browser: in demo il consenso vale solo per questa pagina.'
            : speech ? `Servizi vocali del browser attivi fino al ${new Date(speech.expiresAt).toLocaleDateString('it-IT')}: dettatura dal microfono nella chat.`
            : 'Servizi vocali del browser spenti: li attivi dal microfono o dal simbolo nella chat, con un consenso dedicato.'}
        </Text>
        <Text testID="settings-ai-routes" style={[styles.hint, { color: p.subtle }]}>
          Router AI · dettatura {engineName[routes.speechToText]} · voce di Tao {engineName[routes.textToSpeech]} (prima
          le voci del dispositivo) · piano del giorno {engineName[routes.briefing]}.
        </Text>
      </View>

      {api && (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>CONFIGURAZIONE</Text>
          {settings ? summary(settings).map(line => <Text key={line} style={[styles.hint, heading]}>{line}</Text>)
            : <Text style={[styles.hint, heading]}>Non hai ancora fatto la configurazione guidata.</Text>}
          <View style={styles.row}>
            <Button testID="settings-rerun-setup" label={settings ? 'Rifai la configurazione' : '✨ Configura Tao'} p={p}
              primary={!settings} onPress={onRunSetup} />
            {settings && (
              <Button testID="settings-agent-mode" p={p} disabled={saving}
                label={settings.agentMode === 'read_only' ? 'Permetti azioni con conferma' : 'Torna a sola lettura'}
                onPress={() => void save({
                  ...settings,
                  agentMode: settings.agentMode === 'read_only' ? 'act_with_confirmation' : 'read_only',
                  accounts: settings.accounts.map(a => ({
                    ...a,
                    delegations: settings.agentMode === 'read_only'
                      ? (supported(a.provider).includes('tasks.complete') && a.delegations.includes('tasks.read')
                        ? Array.from(new Set([...a.delegations, 'tasks.complete'])) : a.delegations)
                      : a.delegations.filter(d => d !== 'tasks.complete')
                  }))
                }, settings.agentMode === 'read_only' ? 'Tao può agire, sempre dopo la tua conferma.' : 'Tao ora può solo leggere.')} />
            )}
          </View>
        </View>
      )}

      {api && settings && settings.accounts.length > 0 && (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>COSA PUÒ FARE TAO PER TE, ACCOUNT PER ACCOUNT</Text>
          {settings.accounts.map(account => (
            <View key={account.id} testID="settings-account" style={[styles.accountBlock, { borderColor: p.border }]}>
              <Text style={[styles.name, heading]}>{account.label}</Text>
              <Text style={[styles.hint, { color: p.subtle, marginTop: 2 }]}>
                {account.provider === 'microsoft' ? 'Microsoft' : 'Google'} · {account.usage === 'work' ? 'lavoro' : 'personale'} ·{' '}
                {account.connectedAccountId && connectedIds.has(account.connectedAccountId) ? 'collegato' : 'da collegare'}
              </Text>
              <View style={styles.row}>
                {supported(account.provider)
                  .filter(c => c !== 'tasks.complete' || settings.agentMode === 'act_with_confirmation')
                  .map(capability => {
                    const on = account.delegations.includes(capability);
                    return <Button key={capability} testID={`delegation-${capability}`} p={p} selected={on} disabled={saving}
                      label={(on ? '✓ ' : '') + capabilityNames[capability]} onPress={() => toggle(account, capability)} />;
                  })}
              </View>
            </View>
          ))}
        </View>
      )}

      {api && <ConnectionsPanel api={api} p={p} onChanged={() => { void load(); }} onConnected={linkConnected} />}

      <View style={card}>
        <Text style={[styles.kicker, { color: p.subtle }]}>NOTE E PRIVACY</Text>
        <Text style={[styles.hint, heading]}>Note, consensi, esportazione e cancellazione di tutti i tuoi dati.</Text>
        <View style={styles.row}>
          <Button testID="settings-open-memory" label="Note e privacy" p={p} onPress={onOpenMemory} />
        </View>
      </View>
      {!!message && <Text accessibilityRole="alert" testID="settings-message" style={[styles.hint, heading]}>{message}</Text>}
      <Text style={[styles.footer, { color: p.subtle }]}>
        TAO · 0.5 · PRIVACY BY DESIGN · NESSUNA AZIONE SENZA LA TUA CONFERMA
      </Text>
    </View>
  );
}

function Button({ label, onPress, p, primary, selected, disabled, testID }: {
  label: string; onPress: () => void; p: Theme; primary?: boolean; selected?: boolean; disabled?: boolean;
  testID?: string;
}) {
  return (
    <Pressable testID={testID} accessibilityRole="button" onPress={onPress} disabled={disabled}
      aria-pressed={selected}
      style={[styles.button, {
        backgroundColor: primary ? p.accent : selected ? p.card2 : 'transparent',
        borderColor: primary || selected ? p.accent : p.border, opacity: disabled ? 0.5 : 1
      }]}>
      <Text style={[styles.buttonText, { color: primary ? p.accentText : p.text }]}>{label}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  wrap: { gap: 16, paddingTop: 12, paddingBottom: 40, maxWidth: 720, width: '100%', alignSelf: 'center' },
  card: { borderWidth: 1, borderRadius: 24, padding: 22 },
  accountRow: { flexDirection: 'row', alignItems: 'center', gap: 14 },
  avatar: { width: 48, height: 48, borderRadius: 24, alignItems: 'center', justifyContent: 'center' },
  title: { fontSize: 20, fontWeight: '700' },
  name: { fontSize: 15, fontWeight: '700' },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.4 },
  hint: { fontSize: 13, lineHeight: 19, marginTop: 8 },
  row: { flexDirection: 'row', flexWrap: 'wrap', gap: 8, marginTop: 12 },
  accountBlock: { borderTopWidth: 1, paddingTop: 12, marginTop: 12 },
  button: { borderWidth: 1, borderRadius: 999, paddingHorizontal: 14, paddingVertical: 9 },
  buttonText: { fontSize: 13, fontWeight: '700' },
  footer: { fontSize: 10, letterSpacing: 1.5, textAlign: 'center', marginTop: 12 }
});
