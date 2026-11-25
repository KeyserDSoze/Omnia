import type { ConnectorAccountDto, ConnectorProviderId, MemoryApi } from './memoryApi';
import { stateFromAuthorizationUrl, waitForConnectorCallback } from './connectorCallback';

const AUTHORIZATION_TIMEOUT_MS = 10 * 60_000;

/**
 * Connects a provider account through a popup. The popup must be opened synchronously in the
 * click handler (popup blockers), so callers pass it in. Resolves null when the user cancels.
 */
export async function connectInPopup(api: MemoryApi, provider: ConnectorProviderId, popup: Window,
  signal: AbortSignal): Promise<ConnectorAccountDto | null> {
  try {
    const { authorizationUrl } = await api.authorizeConnector(provider);
    const state = stateFromAuthorizationUrl(authorizationUrl);
    if (!state) throw new Error('invalid_authorization_url');
    const callback = waitForConnectorCallback(state, {
      origin: window.location.origin, timeoutMs: AUTHORIZATION_TIMEOUT_MS, signal, target: window
    });
    popup.location.href = authorizationUrl;
    const result = await callback;
    if (result.error || !result.code) return null;
    return await api.completeConnector(result.state, result.code);
  } finally {
    if (!popup.closed) popup.close();
  }
}

export function openConnectPopup(): Window | null {
  return typeof window === 'undefined' ? null : window.open('', 'tao-connector', 'popup=yes,width=520,height=720');
}
