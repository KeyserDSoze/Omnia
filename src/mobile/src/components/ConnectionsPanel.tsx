import { useEffect, useRef, useState } from 'react';
import { Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import {
  CloudApiError, type ConnectorAccountDto, type ConnectorProviderDto, type ConnectorsDto, type MemoryApi
} from '../services/memoryApi';
import { stateFromAuthorizationUrl, waitForConnectorCallback } from '../services/connectorCallback';
import type { Palette } from './TodayPanel';

const AUTHORIZATION_TIMEOUT_MS = 10 * 60_000;

const capabilityLabels: Record<string, string> = {
  'calendar.read': 'calendario', 'mail.read': 'email', 'tasks.read': 'attività',
  'tasks.complete': 'completare attività (solo su tua conferma)', 'chats.read': 'chat Teams'
};

/**
 * Connected work accounts. OAuth runs in a popup; the API (confidential client) redeems the
 * code. Tokens never reach the browser and are stored sealed on the server.
 */
export function ConnectionsPanel({ api, p, onChanged }: {
  api: MemoryApi;
  p: Palette;
  onChanged: (connectors: ConnectorsDto) => void;
}) {
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
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : 'Connessioni non disponibili.');
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
    if (pending || Platform.OS !== 'web' || typeof window === 'undefined') return;
    // Open synchronously inside the click handler, otherwise popup blockers intervene.
    const popup = window.open('', 'tao-connector', 'popup=yes,width=520,height=720');
    if (!popup) {
      setMessage('Il browser ha bloccato la finestra di accesso: consenti i popup per questo sito.');
      return;
    }
    const controller = new AbortController();
    abort.current = controller;
    setPending(provider.id);
    setMessage('Completa l’accesso a ' + provider.displayName + ' nella finestra aperta…');
    try {
      const { authorizationUrl } = await api.authorizeConnector(provider.id);
      const state = stateFromAuthorizationUrl(authorizationUrl);
      if (!state) throw new Error('invalid_authorization_url');
      const callback = waitForConnectorCallback(state, {
        origin: window.location.origin, timeoutMs: AUTHORIZATION_TIMEOUT_MS,
        signal: controller.signal, target: window
      });
      popup.location.href = authorizationUrl;
      const result = await callback;
      if (!live.current) return;
      if (result.error || !result.code) {
        setMessage('Accesso a ' + provider.displayName + ' annullato o negato.');
        return;
      }
      const account = await api.completeConnector(result.state, result.code);
      if (!live.current) return;
      setMessage(account.displayName + ' collegato.');
      await load();
    } catch (e) {
      if (!live.current) return;
      setMessage(e instanceof CloudApiError ? e.message :
        e instanceof Error && e.message === 'timeout' ? 'Tempo scaduto: riprova il collegamento.' :
        e instanceof Error && e.message === 'aborted' ? 'Collegamento annullato.' :
        'Collegamento non riuscito. Riprova.');
    } finally {
      if (!popup.closed) popup.close();
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
      setMessage(account.displayName + ' scollegato: i token sono stati eliminati da Tao.' +
        (account.provider === 'microsoft' ? ' Puoi revocare anche il consenso da myapps.microsoft.com.' : ''));
      await load();
    } catch (e) {
      if (live.current) setMessage(e instanceof CloudApiError ? e.message : 'Scollegamento non riuscito.');
    }
  }

  const heading = { color: p.text };
  return (
    <View testID="connections-panel" style={[styles.card, { backgroundColor: p.surface, borderColor: p.border }]}>
      <Text style={[styles.kicker, { color: p.subtle }]}>CONNESSIONI DI LAVORO</Text>
      <Text style={[styles.hint, heading]}>
        Tao legge calendario, email, attività e chat per costruire la tua giornata. Non salva i contenuti:
        li legge al momento e conserva solo le tue decisioni. L’unica scrittura possibile è completare
        un’attività, e solo se la confermi.
      </Text>
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
            <Text style={[styles.buttonText, heading]}>{confirming === account.id ? 'Conferma scollegamento' : 'Scollega'}</Text>
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
                {pending === provider.id ? 'In attesa…' : 'Collega ' + provider.displayName}
              </Text>
            </Pressable>
            <Text style={[styles.hint, { color: p.subtle }]}>
              {provider.configured
                ? 'Accesso a: ' + provider.capabilities.map(c => capabilityLabels[c] ?? c).join(', ') + '.'
                : 'Non configurato su questo server (vedi README: registrazione app).'}
            </Text>
          </View>
        ))}
      </View>
      {pending && (
        <Pressable testID="connector-cancel" accessibilityRole="button" onPress={() => abort.current?.abort()}>
          <Text style={[styles.hint, { color: p.accent }]}>Annulla collegamento</Text>
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
