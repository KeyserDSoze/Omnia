export type WebCloudConfig = {
  issuer: string;
  clientId: string;
  apiBaseUrl: string;
};

export type WebMode =
  | { kind: 'demo' }
  | { kind: 'invalid'; reason: string }
  | { kind: 'cloud'; config: WebCloudConfig };

export function isAllowedPublicEndpoint(value: string): boolean {
  try {
    const url = new URL(value);
    const loopback = url.hostname === 'localhost' || url.hostname === '127.0.0.1';
    return (url.protocol === 'https:' || (url.protocol === 'http:' && loopback)) &&
      !url.username && !url.password && !url.search && !url.hash &&
      !value.endsWith('//');
  } catch {
    return false;
  }
}

export function resolveWebMode(raw: { issuer?: string; clientId?: string; apiBaseUrl?: string }): WebMode {
  const issuer = raw.issuer?.trim() ?? '';
  const clientId = raw.clientId?.trim() ?? '';
  const apiBaseUrl = raw.apiBaseUrl?.trim() ?? '';
  if (!issuer && !clientId && !apiBaseUrl) return { kind: 'demo' };
  if (!issuer || !clientId || !apiBaseUrl)
    return { kind: 'invalid', reason: 'Configurazione OIDC/API incompleta.' };
  if (!isAllowedPublicEndpoint(issuer) || !isAllowedPublicEndpoint(apiBaseUrl) ||
      !/^[A-Za-z0-9._:-]{1,120}$/.test(clientId))
    return { kind: 'invalid', reason: 'Indirizzo OIDC/API non sicuro o client ID non valido.' };
  return { kind: 'cloud', config: {
    issuer: issuer.replace(/\/$/, ''),
    clientId,
    apiBaseUrl: apiBaseUrl.replace(/\/$/, '')
  } };
}

export function configuredWebMode(): WebMode {
  // Expo replaces static process.env.EXPO_PUBLIC_* references at build time.
  // These are public client settings, never OAuth client secrets.
  return resolveWebMode({
    issuer: process.env.EXPO_PUBLIC_OMNIA_OIDC_ISSUER,
    clientId: process.env.EXPO_PUBLIC_OMNIA_OIDC_CLIENT_ID,
    apiBaseUrl: process.env.EXPO_PUBLIC_OMNIA_API_URL
  });
}

export function hasOidcCallback(search: string): boolean {
  const query = new URLSearchParams(search);
  return query.has('code') || query.has('error');
}
