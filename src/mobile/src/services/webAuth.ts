import { InMemoryWebStorage, UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { hasOidcCallback, type WebCloudConfig } from './config';

export interface BrowserAuth {
  handleRedirect(): Promise<boolean>;
  hasSession(): Promise<boolean>;
  login(): Promise<void>;
  accessToken(): Promise<string>;
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
  const uri = new URL('/Omnia/', window.location.origin).toString();
  const manager = new UserManager({
    authority: config.issuer,
    client_id: config.clientId,
    redirect_uri: uri,
    post_logout_redirect_uri: uri,
    response_type: 'code',
    scope: 'openid profile',
    loadUserInfo: false,
    automaticSilentRenew: false,
    monitorSession: false,
    userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
    stateStore: new WebStorageStateStore({ store: window.sessionStorage, prefix: 'omnia.pkce.' }),
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
    login: () => manager.signinRedirect(),
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
