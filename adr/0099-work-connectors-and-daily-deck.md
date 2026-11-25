# ADR 0099 — Work connectors and the daily deck

- Date: 2026-11-25
- Status: accepted (implemented on `main`)

## Context

The first release must connect the user's work accounts (Microsoft 365 via Graph, Google Workspace)
and turn the day into a list to triage quickly ("Tinder-like": done / not done / later), with the
option to act on the source app. Personal and third-party content is involved, so storage,
consent and writes must stay minimal and explicit.

## Decision

- **Live read, no mirror.** Calendar, mail, tasks and chats are fetched at request time and never
  stored. Tao stores only the item key (`provider:account:kind:externalId`), the user's decision
  (30-day retention) and an audit row for each provider write (180 days).
- **OAuth as a confidential client on the API**, authorization code + PKCE S256. The browser opens a
  popup and relays `{state, code}` from `/connections/callback` via BroadcastChannel/postMessage
  (no storage); the API redeems the code with the client secret and the sealed verifier. The state is
  stored only as SHA-256, single use, 10-minute lifetime and bound to the signed actor that started it.
- **Sealed credentials.** Tokens and identity are sealed with the envelope key ring, AAD = tenant,
  owner, purpose `connector.credentials` and account id; refresh happens under a row lock.
- **Deterministic priority.** Ordering is an explainable score (overdue task 100 … ended meeting 10),
  not AI, and is labeled as such.
- **Writes only with explicit, per-action confirmation.** The only write in this release is completing
  a To Do / Google Tasks task, available when the user ticks the checkbox on that card; success and
  failure are audited. No automatic sends.
- **Degraded but honest.** Per-account status `ok` / `reconnect_required` / `unavailable`; a missing
  optional permission hides one category; provider error bodies are never surfaced or logged.
- **Data rights.** New tables are FORCE RLS, in the runtime guard, in export (accounts without
  tokens) and in erasure (which first revokes provider grants best effort).
- HTTP clients without SDKs, tested with fake handlers; persistence tested on real PostgreSQL.

## Consequences

- Graph `Chat.Read` and Gmail restricted scopes may need tenant admin consent / Google verification.
- Live fetch costs a few provider calls per deck load; caching is deferred until needed.
- Microsoft has no per-app token revocation: disconnect deletes Tao's tokens and the UI points to
  myapps.microsoft.com.
