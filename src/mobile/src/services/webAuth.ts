import { InMemoryWebStorage, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { hasOidcCallback, type WebCloudConfig } from './config';

export interface BrowserAuth {
  handleRedirect(): Promise<boolean>;
  hasSession(): Promise<boolean>;
  /** Optional Keycloak identity provider hint (social login: google, microsoft). */
  login(idpHint?: 'google' | 'microsoft'): Promise<void>;
  /** Display data from the validated ID token; never used for authorization. */
  profile(): Promise<{ name: string | null; email: string | null }>;
  accessToken(): Promise<string>;
  /** Local-only fail-closed handling when the API rejects bearer auth. */
  discardLocalSession(): Promise<void>;
  logout(): Promise<void>;
}

/**
 * For Expo Web only. The UserManager validates OIDC issuer/nonce/state,
 * PKCE S256 and the token response. User/access/refresh tokens live
 * ONLY in JavaScript memory; sessionStorage is used for short-lived
 * redirect transaction state, never for tokens.
 */
export function createBrowserAuth(config: WebCloudConfig): BrowserAuth {
  if (typeof window === 'undefined') throw new Error('OIDC Web richiede un browser.');
  // EXPO_BASE_URL is inlined from experiments.baseUrl ('' on the gateway, '/Tao' on Pages).
  const base = (process.env.EXPO_BASE_URL ?? '').replace(/\/+$/, '');
  const uri = new URL(`${base}/`, window.location.origin).toString();
  const manager = new UserManager({
    authority: config.issuer,
    client_id: config.clientId,
    redirect_uri: uri,
    post_logout_redirect_uri: uri,
    response_type: 'code',
    scope: 'openid',
    loadUserInfo: false,
    automaticSilentRenew: false,
    monitorSession: false,
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    stateStore: new WebStorageStateStore({ store: window.sessionStorage, prefix: 'tao.pkce.' }),
    revokeTokensOnSignout: true
  });
  return {
    async handleRedirect() {
      if (!hasOidcCallback(window.location.search)) return false;
      try {
        const user = await manager.signinRedirectCallback();
        if (!user.access_token || user.expired) throw new Error('Access token scaduto o mancante.');
        return true;
      } finally {
        // Never leave an OAuth authorization code in address bar or browser history.
        window.history.replaceState(window.history.state, '', uri);
      }
    },
    async hasSession() {
      const user = await manager.getUser();
      return Boolean(user?.access_token && !user.expired);
    },
    login: idpHint => manager.signinRedirect(idpHint ? { extraQueryParams: { kc_idp_hint: idpHint } } : undefined),
    async profile() {
      const claims = (await manager.getUser())?.profile;
      const text = (value: unknown) => typeof value === 'string' && value.trim() ? value.trim() : null;
      return { name: text(claims?.name) ?? text(claims?.preferred_username), email: text(claims?.email) };
    },
    async accessToken() {
      const user = await manager.getUser();
      if (!user) throw new Error('Accesso richiesto.');
      if (user.expires_in !== undefined && user.expires_in < 45) {
        try {
          const refreshed = await manager.signinSilent();
          if (!refreshed?.access_token || refreshed.expired) throw new Error('Sessione scaduta.');
          return refreshed.access_token;
        } catch {
          await manager.removeUser();
          throw new Error('Sessione scaduta. Accedi di nuovo.');
        }
      }
      if (!user.access_token || user.expired) {
        await manager.removeUser();
        throw new Error('Sessione scaduta. Accedi di nuovo.');
      }
      return user.access_token;
    },
    discardLocalSession: () => manager.removeUser(),
    async logout() {
      try {
        // Revocation is best effort. Even if offline, clear the only local token store.
        await manager.revokeTokens(['refresh_token', 'access_token']);
      } finally {
        await manager.removeUser();
      }
    }
  };
}
