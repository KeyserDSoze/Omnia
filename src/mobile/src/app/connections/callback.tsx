import { useEffect, useState } from 'react';
import { Platform, Text, View } from 'react-native';
import { parseConnectorCallback, publishConnectorCallback } from '../../services/connectorCallback';
import { useT, type MessageKey } from '../../i18n';

/**
 * OAuth redirect target for work connectors, opened in a popup. It relays {state, code} to the
 * Tao window that started the flow and closes. The one-time code is removed from the address
 * bar immediately and never written to storage; it is useless without the server secret.
 */
export default function ConnectorCallbackPage() {
  const t = useT();
  const [message, setMessage] = useState<MessageKey>('callback.working');

  useEffect(() => {
    if (Platform.OS !== 'web' || typeof window === 'undefined') return;
    const callback = parseConnectorCallback(window.location.search);
    window.history.replaceState(null, '', window.location.pathname);
    if (!callback) {
      setMessage('callback.invalid');
      return;
    }
    publishConnectorCallback(callback, window.location.origin, window.opener as Window | null);
    setMessage(callback.error ? 'callback.denied' : 'callback.done');
    const timer = setTimeout(() => window.close(), 800);
    return () => clearTimeout(timer);
  }, []);

  return (
    <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 }}>
      <Text testID="connector-callback" style={{ fontSize: 16, lineHeight: 24, textAlign: 'center', maxWidth: 420 }}>
        {t(message)}
      </Text>
    </View>
  );
}
