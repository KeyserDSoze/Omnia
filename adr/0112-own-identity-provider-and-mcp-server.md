# ADR 0112 — Tao as its own identity provider, and an MCP server for other AIs

- Date: 2026-11-26
- Status: accepted for direction; step 1 (the MCP server on the current sign-in) is implemented, the rest is planned in epic #240

## Context

Tao signs users in with Keycloak (ADR 0104, 0105): it gave PKCE, JWKS, social sign-in and an admin
console without writing security code, and the API only has to trust a standard issuer. Running it
on Azure showed the price:

- a JVM container that takes minutes to start and shares the always-on plan with the API;
- its own database, theme, realm import that runs once, and a public admin page;
- a long chain of settings to keep in step (hostname, client redirects, social providers).

Its main strength, password and user management, is not used: sign-in is social only, so Keycloak
stores no password at all. At the same time Tao now wants to be used by **other AIs** through an MCP
server. MCP clients authorise with OAuth 2.1 (PKCE, protected-resource metadata, dynamic client
registration, resource indicators) and the user must decide, per client, what it may read. That
decision belongs in Tao's own consent ledger, next to everything else the user authorised.

## Decision

1. **Tao becomes its own OAuth 2.1 / OpenID Connect authorization server** inside `Tao.Api`, built
   on OpenIddict (Apache-2.0), not on hand-written protocol code. Users, external logins, clients,
   authorizations and tokens live in PostgreSQL.
2. **Social sign-in only** (Microsoft, Google) through the ASP.NET external authentication handlers.
   No passwords, no recovery flow. A user is linked by the provider's stable subject, never by
   e-mail address; a second provider is linked only while the user is signed in, on purpose.
3. **Stable `sub`.** The subject is a GUID stored in `identity_users`. Users that exist today keep
   their Keycloak `sub` (`legacy_subject` is the same value), so every row keyed by owner stays
   reachable after the cut-over. `tenant_id` comes from the stored user, never from a request.
4. **Tokens.** Short-lived signed JWT access tokens (15 minutes, ES256, key from Key Vault with
   rotation), opaque rotating refresh tokens with reuse detection (a replay revokes the family),
   one-time authorization codes of 60 seconds. Claims: `sub`, `tenant_id`, `aud` (the API), `scope`.
5. **Clients.** `tao-web` is a public client with PKCE S256 and exact redirect matching. MCP clients
   register through Dynamic Client Registration (RFC 7591), limited to public clients, S256 only,
   https or loopback redirects, rate-limited, and restricted to the `mcp:*` scopes. For each client
   the user sees a consent page (name, redirect host, scopes). The answer is written to the consent
   ledger (purpose `mcp.access`) and can be revoked in the app.
6. **MCP server** (Streamable HTTP, stateless) at `/api/v1/mcp` with RFC 9728 metadata at
   `/.well-known/oauth-protected-resource`. Its tools are Tao's own use cases. They are read-only
   first; writes follow the repository rule (one explicit confirmation per action, audited). Every
   result is labelled as untrusted data, because it comes from mail and chats.
7. **Sign-in and consent pages** are served by the API in the look of the app. The Keycloak theme
   is dropped with Keycloak.
8. **Order of work**, so nothing waits for the risky part:
   1. MCP server on the current Keycloak sign-in (done here);
   2. the authorization server and social sign-in next to Keycloak, switchable by configuration;
   3. dynamic client registration, consent page and revocation;
   4. import of the existing users, cut-over, removal of Keycloak from compose, Terraform, scripts
      and docs.

## Non-negotiable rules for the identity code

- Only authorization code with PKCE S256 (+ refresh). No implicit, no password, no device grant.
- Exact redirect URI comparison; `state` and `nonce` checked; issuer and audience validated.
- Rate limits on the token, registration and callback endpoints; registration size caps.
- Sign-in, consent and token events are audited (no tokens, no addresses in the audit rows).
- Identity tables follow the repository rules: encrypted personal fields, retention, export and
  erasure, and tests on a real PostgreSQL. Because a login happens before the actor is known, they
  use a dedicated database role and schema instead of the actor-scoped policies, with tests that
  the application role cannot read them.
- External review of the flows and an abuse-case test suite before Keycloak is removed.

## Alternatives considered

- **Keep Keycloak and add only the MCP resource server.** Cheapest, and it is step 1. Rejected as the
  end state: per-client consent in Tao's ledger and dynamic registration tied to Tao users would
  need Java extensions, and the container stays.
- **Entra External ID, Auth0, Clerk.** Rejected: cost, data location, and the MCP consent still
  has to live in Tao.
- **Duende IdentityServer.** Rejected: commercial licence.
- **Hand-written OAuth server.** Rejected: protocol code is where subtle vulnerabilities live.

## Consequences

- Tao owns security-critical code. Mitigation: a mature library, no passwords, a small surface
  (social login, one web client, MCP clients), abuse-case tests and review.
- One container less (about 1 GB of memory on the shared plan) and one system less to configure.
- Any MCP-capable AI can use Tao with the user's explicit, revocable consent.
- Until step 4 both issuers may coexist; the API accepts the configured issuer(s) explicitly.

Tracking: epic #240 and its issues in `docs/backlog.md`.
