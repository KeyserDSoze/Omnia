import { useEffect, useRef, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, MCP_PURPOSE, MCP_SOURCE, SPEECH_PURPOSE, SPEECH_SOURCE, type ConnectorAccountDto, type ConsentDto,
  type MemoryApi,
  type SettingsResponseDto
} from '../services/memoryApi';
import { newId, summary, toSaveBody, type DraftAccount, type SettingsDto } from '../services/setupFlow';
import type { AiEngine } from '../services/aiRouter';
import { intlTag, localeInfo, useLocale, useT, type MessageKey } from '../i18n';
import { ConnectionsPanel } from './ConnectionsPanel';
import { AppPanel } from './AppPanel';
import { RemindersPanel } from './RemindersPanel';
import { Button } from './Button';
import type { AppUpdates } from '../services/useAppUpdates';
import type { PushNotifications } from '../services/usePushNotifications';
import { LanguagePicker } from './LanguagePicker';
import type { VoiceControl } from './ChatHome';
import { web, type Theme } from './theme';

const capabilityKeys = new Set<string>(['calendar.read', 'mail.read', 'tasks.read', 'chats.read', 'tasks.complete']);

/**
 * Account and settings (top right). Everything shown here is what the agent may read or do on
 * behalf of the user, per account; changes are saved immediately to the server. Without an
 * account (demo) only appearance, voice and notes are available.
 */
export function SettingsPanel({ api, theme: p, profile, accountConsoleUrl, onLogout, onOpenMemory, onRunSetup,
  onToggleTheme, voice, updates, push }: {
  api: MemoryApi | null;
  theme: Theme;
  profile: { name: string | null; email: string | null } | null;
  accountConsoleUrl: string | null;
  onLogout: (() => void) | null;
  onOpenMemory: () => void;
  onRunSetup: () => void;
  onToggleTheme: () => void;
  voice: VoiceControl;
  updates: AppUpdates;
  push: PushNotifications;
}) {
  const t = useT();
  const locale = useLocale();
  const capabilityName = (c: string) => capabilityKeys.has(c) ? t(`capability.${c}` as MessageKey) : c;
  const engineName = (engine: AiEngine) => t(`engine.${engine}`);
  const [state, setState] = useState<SettingsResponseDto | null>(null);
  const [speech, setSpeech] = useState<ConsentDto | null>(null);
  const [mcp, setMcp] = useState<ConsentDto | null>(null);
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
      setMcp(grants.find(g => g.source === MCP_SOURCE && g.purpose === MCP_PURPOSE && g.active) ?? null);
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.loadFailed'));
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
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.saveFailed'));
    } finally {
      if (live.current) setSaving(false);
    }
  }

  async function toggleVoice() {
    const tts = voice.router.textToSpeech;
    if (!tts) return;
    if (voice.on) { voice.setOn(false); setMessage(t('settings.voiceStopped')); return; }
    const lang = localeInfo(locale).tag;
    const kind = await tts.availability(lang);
    if (!live.current) return;
    if (kind === 'none') { setMessage(t('voice.none')); return; }
    if (kind === 'online' && !speech) {
      setMessage(t('settings.voiceNeedsConsent'));
      return;
    }
    await tts.ready(kind === 'online', lang);
    voice.setOn(true);
    if (live.current) setMessage(t(kind === 'local' ? 'settings.voiceLocal' : 'settings.voiceOnline'));
  }

  async function revokeSpeech() {
    if (!api || !speech) return;
    setSaving(true);
    try {
      await api.revokeConsent(speech.id);
      if (!live.current) return;
      setSpeech(null);
      if (await voice.router.textToSpeech?.availability(localeInfo(locale).tag) === 'online') voice.setOn(false);
      setMessage(t('settings.speechRevoked'));
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.revokeFailed'));
    } finally {
      if (live.current) setSaving(false);
    }
  }

  async function toggleMcp() {
    if (!api) return;
    setSaving(true);
    setMessage('');
    try {
      if (mcp) {
        await api.revokeConsent(mcp.id);
        if (live.current) setMcp(null);
      } else {
        const grant = await api.grantMcpAccess();
        if (live.current) setMcp(grant);
      }
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('settings.saveFailed'));
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
    }, t(on ? 'settings.delegationOff' : 'settings.delegationOn', { account: account.label, capability: capabilityName(capability) }));
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
    void save({ ...settings, accounts }, t('settings.accountAdded', { account: account.displayName }));
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
              <Text testID="settings-name" style={[styles.title, heading]}>{profile.name ?? t('settings.yourAccount')}</Text>
              {!!profile.email && <Text style={[styles.hint, { color: p.subtle, marginTop: 2 }]}>{profile.email}</Text>}
            </View>
          </View>
          <View style={styles.row}>
            {accountConsoleUrl && (
              <Button testID="settings-account-console" label={t('settings.console')} p={p}
                onPress={() => { if (typeof window !== 'undefined') window.open(accountConsoleUrl, '_blank', 'noopener'); }} />
            )}
            {onLogout && <Button testID="settings-logout" label={t('settings.logout')} p={p} onPress={onLogout} />}
          </View>
        </View>
      ) : (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.demoKicker')}</Text>
          <Text style={[styles.hint, heading]}>{t('settings.demoText')}</Text>
        </View>
      )}

      <View style={card}>
        <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.languageKicker')}</Text>
        <View style={{ marginTop: 12 }}><LanguagePicker theme={p} /></View>
        <Text style={[styles.hint, { color: p.subtle }]}>{t('settings.languageHint')}</Text>
      </View>

      <View style={card}>
        <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.appearanceKicker')}</Text>
        <View style={styles.row}>
          <Button testID="theme-toggle" p={p} onPress={onToggleTheme}
            label={p.mode === 'dark' ? t('settings.themeLight') : t('settings.themeDark')} />
          {voice.router.textToSpeech && (
            <Button testID="settings-voice" p={p} selected={voice.on} onPress={() => { void toggleVoice(); }}
              label={voice.on ? t('settings.voiceOn') : t('settings.voiceOff')} />
          )}
          {api && speech && (
            <Button testID="settings-speech-revoke" p={p} disabled={saving} onPress={() => { void revokeSpeech(); }}
              label={t('settings.speechRevoke')} />
          )}
        </View>
        <Text testID="settings-speech" style={[styles.hint, heading]}>
          {!voice.router.speechToText && !voice.router.textToSpeech
            ? t('settings.speechUnavailable')
            : !api ? t('settings.speechDemo')
            : speech ? t('settings.speechActive', { date: new Date(speech.expiresAt).toLocaleDateString(intlTag()) })
            : t('settings.speechOff')}
        </Text>
        <Text testID="settings-ai-routes" style={[styles.hint, { color: p.subtle }]}>
          {t('settings.routes', { stt: engineName(routes.speechToText), tts: engineName(routes.textToSpeech),
            briefing: engineName(routes.briefing) })}
        </Text>
      </View>

      {api && (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.mcpKicker')}</Text>
          <Text testID="settings-mcp" style={[styles.hint, heading]}>
            {mcp ? t('settings.mcpActive', { date: new Date(mcp.expiresAt).toLocaleDateString(intlTag()) }) : t('settings.mcpOff')}
          </Text>
          {!!mcp && <Text testID="settings-mcp-url" selectable style={[styles.hint, { color: p.subtle }]}>
            {t('settings.mcpUrl', { url: api.mcpUrl() })}
          </Text>}
          <View style={styles.row}>
            <Button testID="settings-mcp-toggle" p={p} primary={!mcp} disabled={saving}
              label={t(mcp ? 'settings.mcpRevoke' : 'settings.mcpGrant')} onPress={() => { void toggleMcp(); }} />
          </View>
        </View>
      )}

      {api && (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.configKicker')}</Text>
          {settings ? summary(settings).map(line => <Text key={line} style={[styles.hint, heading]}>{line}</Text>)
            : <Text style={[styles.hint, heading]}>{t('settings.noSetup')}</Text>}
          <View style={styles.row}>
            <Button testID="settings-rerun-setup" label={settings ? t('settings.rerun') : t('hint.setup')} p={p}
              primary={!settings} onPress={onRunSetup} />
            {settings && (
              <Button testID="settings-agent-mode" p={p} disabled={saving}
                label={settings.agentMode === 'read_only' ? t('settings.allowActions') : t('settings.readOnly')}
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
                }, t(settings.agentMode === 'read_only' ? 'settings.actionsOn' : 'settings.actionsOff'))} />
            )}
          </View>
        </View>
      )}

      {api && settings && settings.accounts.length > 0 && (
        <View style={card}>
          <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.delegationsKicker')}</Text>
          {settings.accounts.map(account => (
            <View key={account.id} testID="settings-account" style={[styles.accountBlock, { borderColor: p.border }]}>
              <Text style={[styles.name, heading]}>{account.label}</Text>
              <Text style={[styles.hint, { color: p.subtle, marginTop: 2 }]}>
                {t('settings.accountLine', {
                  provider: account.provider === 'microsoft' ? 'Microsoft' : 'Google',
                  usage: t(account.usage === 'work' ? 'settings.work' : 'settings.personal'),
                  state: t(account.connectedAccountId && connectedIds.has(account.connectedAccountId) ? 'settings.linked' : 'settings.toLink')
                })}
              </Text>
              <View style={styles.row}>
                {supported(account.provider)
                  .filter(c => c !== 'tasks.complete' || settings.agentMode === 'act_with_confirmation')
                  .map(capability => {
                    const on = account.delegations.includes(capability);
                    return <Button key={capability} testID={`delegation-${capability}`} p={p} selected={on} disabled={saving}
                      label={(on ? '✓ ' : '') + capabilityName(capability)} onPress={() => toggle(account, capability)} />;
                  })}
              </View>
            </View>
          ))}
        </View>
      )}

      {api && <ConnectionsPanel api={api} p={p} onChanged={() => { void load(); }} onConnected={linkConnected} />}

      {api && <RemindersPanel api={api} push={push} theme={p} />}

      <View style={card}>
        <Text style={[styles.kicker, { color: p.subtle }]}>{t('settings.notesKicker')}</Text>
        <Text style={[styles.hint, heading]}>{t('settings.notesText')}</Text>
        <View style={styles.row}>
          <Button testID="settings-open-memory" label={t('settings.notesButton')} p={p} onPress={onOpenMemory} />
        </View>
      </View>
      {!!message && <Text accessibilityRole="alert" testID="settings-message" style={[styles.hint, heading]}>{message}</Text>}
      <AppPanel updates={updates} theme={p} />
      <Text style={[styles.footer, { color: p.subtle }]}>{t('settings.footer')}</Text>
    </View>
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
  footer: { fontSize: 10, letterSpacing: 1.5, textAlign: 'center', marginTop: 12 }
});
