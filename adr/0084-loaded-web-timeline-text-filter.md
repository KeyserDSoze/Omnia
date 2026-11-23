# ADR 0084 — Transient loaded-page text filter for signed Web timeline

- Date: 2026-11-23
- Status: implemented slice; verified CI evidence to be attached to #187
- Related: #79, epic #7; dependencies #41/#17/#2
- Existing: ADR 0069–0073 (session, 401, keyset), 0080–0083 (filters and grouping)

## Context

The authenticated PostgreSQL timeline offers source, keyword tag and
device-local day filters, but not a text predicate over visible rows.
GET /api/v1/memories/page yields only a limited sorted prefix of actor-scoped
data under signed OIDC JWT and PostgreSQL FORCE RLS. A local search must never
be presented as a global/archive/semantic retrieval.

## Decision

- Transient max-100-character accessible React Native TextInput, only in
  authenticated Web cloud view with loaded notes; search the very same
  LocalMemo.content (API-returned summary) that the user sees.
- Match a contiguous substring with Unicode NFD, strip common Latin combining
  accents, Italian locale-lowercase, collapse whitespace. This is neither
  model-generated insight nor fuzzy/semantic search.
- Apply text AND source AND tag AND device-local day before day grouping.
  Preserve canonical row ID/occurredAt/ordering, cursor, deduplication,
  consent and signed actor RLS. No additional fetch or background crawling.
- Explicit Carica altre note remains accessible with zero matches. Counts and
  choices refer only to already-loaded rows, never the full archive.
- Clear text with private notes on current-session logout, API 401 and
  successful primary PostgreSQL erasure. Preserve CloudViewEpoch guards.
  Never store query or content in URL, analytics, localStorage or AsyncStorage.

## Verification and residual risk

RED: four Vitest tests and real Keycloak Chromium scenario appended to #185
on commit 7acec4b, failing before implementation. GREEN implements pure TS
helper and React Native field. Exact CI/Playwright/Pages SHA and URLs must
be attached to #187 before its closure.

No server search, provider API, native sync, hidden raw search, encryption or
data-rights extension; no assertion of GDPR release readiness. Parents
#79/#7/#41/#17/#2 remain OPEN.

References: https://reactnative.dev/docs/textinput and
https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/String/normalize.
