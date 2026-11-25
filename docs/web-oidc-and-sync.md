# Tao Web — OIDC Authorization Code + PKCE S256 and real memory API (first slice)

Status: **implementation slice** of #39/#41. Browser only, pending real OIDC→API→DB integration confirmation and production/privacy gates. `src/mobile/src/services/webAuth.ts` uses maintained `oidc-client-ts` to validate state/nonce/issuer and manage code + PKCE; does not implement OAuth cryptography by hand.

## Same-origin gateway mode (on-prem)

The on-prem image is built with `EXPO_PUBLIC_TAO_SAME_ORIGIN=true` and base path `''`: issuer
`<origin>/auth/realms/tao`, client `tao-web` and API `<origin>` are derived from the page origin, so
no URL is baked into the bundle. Keycloak's realm is `docker/keycloak/tao-realm.json` (redirect URIs
and web origins from `TAO_PUBLIC_URL`).

## Public static Expo configuration (separate API origin)

Build-time public settings (all required to activate cloud mode):

```sh
EXPO_PUBLIC_TAO_OIDC_ISSUER=https://your-idp.example/realms/tao
EXPO_PUBLIC_TAO_OIDC_CLIENT_ID=tao-web
EXPO_PUBLIC_TAO_API_URL=https://your-api.example
```

- `EXPO_PUBLIC_*` are *public*. Never include secret/password, client_secret, client certificate or app refresh token in these values, GitHub Pages or git. Enable HTTPS only off loopback.
- Register public OIDC client with Authorization Code and PKCE S256. Redirect URI `https://keyserdsoze.github.io/Tao/` (or `http://127.0.0.1:19006/Tao/`/localhost dev); disable implicit and password grants. Configure API audience JWT claim `tao-api` and custom `tenant_id` mapped from trusted issuer, not editable browser values.
- API must validate bearer JWT via JWKS, issuer, audience, expiry and one unique `sub` + `tenant_id`. CORS must allow only the app's exact origin and no credentials.
- For local development with a separate API, configure a realm like `docker/keycloak/tao-realm.json`; use a synthetic local account. SPA relies on OIDC discovery/token endpoint CORS allowed by Keycloak client `webOrigins`.
- No environment vars in Pages CI → explicitly local demo only, not cloud persistence. All three vars set → cloud mode; partial configuration → error, no silent fallback.

## Browser security

1. `signinRedirect()` starts an Authorization Code + PKCE S256 flow and stores short-lived OAuth state in **sessionStorage**. No bearer/refresh tokens are stored there.
2. `signinRedirectCallback()` validates issuer/nonce/state, exchanges code for tokens and clears the URL. Identity is validated **again** at the API, never trusted from client claims.
3. Access+refresh tokens are stored in `InMemoryWebStorage` only. Browser reload loses the session and requests a new provider login (the IdP may maintain its SSO cookie). Expiring token refresh is attempted while current page remains active; failures require re-login.
4. Logout attempts token revocation, clears RAM and displayed data even if network fails. A full IdP SSO logout is a separate feature.
5. All user data calls `fetch` HTTPS API only with `Authorization: Bearer`, `redirect: 'error'`, `credentials: 'omit'`. All network or API errors remain explicit; no false success.

## User flow

1. Authenticate → `GET /api/v1/consents` and `GET /api/v1/memories` → show existing timeline (no raw events in localStorage).
2. For new notes, user checks the 30-day first-party `manual`/ `memory.capture` acknowledgement and presses activate → `POST /api/v1/consents`. This **does not** authorize recordings of others or external connector AI.
3. `POST /api/v1/memories` includes `source=manual`, idempotent `externalId` per attempt, `contentType=text/plain`, content and occurredAt; only a successful API confirmation clears draft and displays saved. Fetch timeline from server after success.
4. Revoke grant via `DELETE /api/v1/consents/{id}` → no new capture; existing notes remain visible until delete/export (#31/#32).

## Not yet supported

Native OIDC+secure storage (#40), real Azure deployment, offline encrypted queue (#117), RLS (#30), source deletion and export (#31), third-party consent, tenant self-provisioning, push, audio upload (#47), global IdP logout and full privacy/recording law review. Public Pages continues to be a demo, not a production-ready personal data service.

## Tests

Vitest `tests/mobile-unit/web-cloud.test.ts` verifies public configuration and API error/no secret handling. Playwright browser E2E for static /Tao/ remains in `src/mobile/e2e`; a separate real Keycloak/API/Postgres job is required to claim end-to-end OAuth verification (#17). Never claim E2E real auth solely from demo page tests.

## Independent manual capture IDs and retry identity (#177 / ADR 0076)

The browser Web cloud source key is now `local-<random UUID v4>`,
generated using secure-context `globalThis.crypto.randomUUID()` for
**each independent capture**; it is no longer derived solely from
`occurredAt`. Two notes in the same millisecond do not reuse a source
key. This ID is a *deduplication identifier*, not an authentication
credential or a tenant selector. The authenticated API remains responsible
for tenant/subject, consent, RLS and immutable-key conflict handling (#175).

An uncertain POST/network retry MUST reuse the previously normalized
entry from the Web `retryRef`, including exact `externalId`,
`content` and `occurredAt`. A newly initiated capture receives a new
UUID. If browser Web Crypto UUIDs are unavailable, cloud capture fails
closed with a visible message; **no timestamp-only or `Math.random`
fallback may be uploaded**. The sequenced `demo-` ID is limited to
in-memory native/demo notes and has no multi-device uniqueness promise.
Native encrypted offline sync needs a secure native generator (e.g.
Expo Crypto) and device testing before release.

RED/GREEN verification: Vitest fixed-timestamp key tests, real Chromium →
Keycloak PKCE → JWT/API → PostgreSQL two-note same-instant E2E and GitHub
Actions linked from #177. This does not implement offline queue, eventual
sync, editing, full provenance dedup or GDPR completeness.
