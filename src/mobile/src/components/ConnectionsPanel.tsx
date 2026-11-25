import { useEffect, useRef, useState } from 'react';
import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, type ConnectorAccountDto, type ConnectorProviderDto, type ConnectorsDto, type MemoryApi
} from '../services/memoryApi';
import { connectInPopup, openConnectPopup } from '../services/connectFlow';
import { useT, type MessageKey } from '../i18n';
import type { Palette } from './TodayPanel';

const capabilityKeys = new Set<string>(['calendar.read', 'mail.read', 'tasks.read', 'chats.read', 'tasks.complete']);

/**
 * Connected work accounts. OAuth runs in a popup; the API (confidential client) redeems the
 * code. Tokens never reach the browser and are stored sealed on the server.
 */
export function ConnectionsPanel({ api, p, onChanged, onConnected }: {
  api: MemoryApi;
  p: Palette;
  onChanged: (connectors: ConnectorsDto) => void;
  onConnected?: (account: ConnectorAccountDto) => void;
}) {
  const t = useT();
  const capability = (c: string) => capabilityKeys.has(c) ? t(`capability.${c}` as MessageKey) : c;
  const [data, setData] = useState<ConnectorsDto | null>(null);
  const [pending, setPending] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<string | null>(null);
  const [message, setMessage] = useState('');
  const live = useRef(true);
  const abort = useRef<AbortController | null>(null);

  async function load() {
    try {
      const result = await api.connectors();
      if (!live.current) return;
      setData(result);
      onChanged(result);
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('connections.loadFailed'));
    }
  }

  useEffect(() => {
    live.current = true;
    void load();
    return () => {
      live.current = false;
      abort.current?.abort();
    };
  }, [api]);

  async function connect(provider: ConnectorProviderDto) {
    if (pending || Platform.OS !== 'web') return;
    // Open synchronously inside the click handler, otherwise popup blockers intervene.
    const popup = openConnectPopup();
    if (!popup) {
      setMessage(t('connections.popupBlocked'));
      return;
    }
    const controller = new AbortController();
    abort.current = controller;
    setPending(provider.id);
    setMessage(t('connections.inProgress', { provider: provider.displayName }));
    try {
      const account = await connectInPopup(api, provider.id, popup, controller.signal);
      if (!live.current) return;
      if (!account) {
        setMessage(t('connections.cancelled', { provider: provider.displayName }));
        return;
      }
      setMessage(t('connections.connected', { account: account.displayName }));
      onConnected?.(account);
      await load();
    } catch (e) {
      if (!live.current) return;
      setMessage(e instanceof CloudApiError ? e.message :
        e instanceof Error && e.message === 'timeout' ? t('connections.timeout') :
        e instanceof Error && e.message === 'aborted' ? t('connections.aborted') :
        t('connections.failed'));
    } finally {
      if (abort.current === controller) abort.current = null;
      if (live.current) setPending(null);
    }
  }

  async function disconnect(account: ConnectorAccountDto) {
    if (confirming !== account.id) {
      setConfirming(account.id);
      return;
    }
    setConfirming(null);
    try {
      await api.disconnectConnector(account.id);
      if (!live.current) return;
      setMessage(t('connections.disconnected', { account: account.displayName }) +
        (account.provider === 'microsoft' ? ' ' + t('connections.microsoftRevoke') : ''));
      await load();
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : t('connections.disconnectFailed'));
    }
  }

  const heading = { color: p.text };
  return (
    <View testID="connections-panel" style={[styles.card, { backgroundColor: p.surface, borderColor: p.border }]}>
      <Text style={[styles.kicker, { color: p.subtle }]}>{t('connections.kicker')}</Text>
      <Text style={[styles.hint, heading]}>{t('connections.intro')}</Text>
      {(data?.accounts ?? []).map(account => (
        <View key={account.id} testID="connector-account" style={[styles.row, { borderColor: p.border }]}>
          <View style={{ flex: 1 }}>
            <Text style={[styles.name, heading]}>{account.displayName}</Text>
            <Text style={[styles.hint, { color: p.subtle, marginTop: 2 }]}>
              {(data?.providers.find(x => x.id === account.provider)?.displayName ?? account.provider)}
              {account.email ? ' · ' + account.email : ''}
            </Text>
          </View>
          <Pressable testID="connector-disconnect" accessibilityRole="button" onPress={() => { void disconnect(account); }}
            style={[styles.button, { backgroundColor: p.card2 }]}>
            <Text style={[styles.buttonText, heading]}>{confirming === account.id ? t('connections.confirmDisconnect') : t('connections.disconnect')}</Text>
          </Pressable>
        </View>
      ))}
      <View style={styles.providers}>
        {(data?.providers ?? []).map(provider => (
          <View key={provider.id} style={{ flexGrow: 1, minWidth: 220 }}>
            <Pressable testID={'connector-connect-' + provider.id} accessibilityRole="button"
              disabled={!provider.configured || pending !== null}
              onPress={() => { void connect(provider); }}
              style={[styles.button, { backgroundColor: p.card2, opacity: provider.configured && !pending ? 1 : 0.5 }]}>
              <Text style={[styles.buttonText, heading]}>
                {pending === provider.id ? t('connections.waiting') : t('connections.connect', { provider: provider.displayName })}
              </Text>
            </Pressable>
            <Text style={[styles.hint, { color: p.subtle }]}>
              {provider.configured
                ? t('connections.access', { list: provider.capabilities.map(capability).join(', ') })
                : t('connections.notConfigured')}
            </Text>
          </View>
        ))}
      </View>
      {pending && (
        <Pressable testID="connector-cancel" accessibilityRole="button" onPress={() => abort.current?.abort()}>
          <Text style={[styles.hint, { color: p.accent }]}>{t('connections.cancel')}</Text>
        </Pressable>
      )}
      {!!message && <Text accessibilityRole="alert" testID="connector-message" style={[styles.hint, heading]}>{message}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  card: { borderWidth: 1, borderRadius: 25, padding: 26, marginBottom: 20 },
  kicker: { fontSize: 11, fontWeight: '800', letterSpacing: 1.2 },
  hint: { fontSize: 12, lineHeight: 18, marginTop: 8 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 12, borderTopWidth: 1, paddingVertical: 12, marginTop: 12 },
  name: { fontSize: 15, fontWeight: '700' },
  providers: { flexDirection: 'row', flexWrap: 'wrap', gap: 12, marginTop: 16 },
  button: { minHeight: 44, paddingHorizontal: 14, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  buttonText: { fontSize: 14, fontWeight: '700' }
});
