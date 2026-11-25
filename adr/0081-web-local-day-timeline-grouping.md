# ADR 0081 — Local calendar day grouping for already-loaded memory timeline

- Date: 2026-11-23
- Status: implemented (acceptance evidence and Actions in issue #183)
- Parent: #79 (timeline UX), #7 (UX), #41 (authenticated Web)
- Related: ADR 0069–0071 (session fences), 0073 (signed keyset pages),
  0080 (loaded-page source filter)
- Timezone reference: https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Intl/DateTimeFormat/formatToParts

## Context

Tao already returns canonical occurredAt instants and page cursors
ordered by (occurred_at DESC, id DESC) from the authenticated,
FORCE-RLS PostgreSQL actor scope. Its Web timeline only showed the
per-row clock, and an explicit source selector filters rows already
loaded. A UTC-date slice, arbitrary 24-hour windows or server timezone
would mislabel the user's calendar day around local midnight and DST.

## Decision

- Preserve the **original ISO occurredAt** field in the display
  projection. Never infer the date from a localized HH:mm clock
  or the ingestion/received-at time. The local demo capture also
  carries its original occurredAt.
- Pure `groupTimelineByLocalDay` receives the **already-filtered**
  list. `Intl.DateTimeFormat.formatToParts` constructs a
  Gregorian calendar date key `YYYY-MM-DD` using the device's
  timezone, and an Italian date heading with the same timezone.
  The explicit IANA timezone parameter is supported in unit tests
  for DST and midnight boundaries; production defaults to local.
- Keep the original descending server order **within and between
  days**; no client resorting, backend changes, re-fetching,
  automatic pagination, generated summaries or new data access.
  Map grouping merges a day spanning two adjacent paginated lists
  into exactly one heading.
- The day heading is a React Native Text with accessible header
  role and stable key. Under each day keep the real clock,
  canonical source label and existing tags, without pretending
  the synthetic tags are inferred AI insight.
- Existing `CloudViewEpoch` and `MemoryApi` fences still clear
  both rows and derived headings on logout, current-session 401,
  erasure, refresh or a switched account. There is no independent
  day state to leak or retain; no browser storage or URL keys.

## TDD and limitations

- RED: Vitest pure helper tests fail because helper is missing;
  Keycloak Chromium + signed PostgreSQL 13-note scenario fails
  because no day group is rendered (Actions in #183).
- GREEN: same deterministic pure tests, 14 actual signed E2E
  scenarios including 13 synthetic captures across two days,
  page boundary merging, source filter and logout wipe;
  test suite/Pages/PITR checks required on the same final SHA.
- These are **device-local display dates**. Tao does not
  preserve an original source timezone or calendar confidence
  per event yet. Two devices in different timezones may assign
  the same instant to different calendar days; this does not
  modify canonical timestamps or tenant scope.
- Not implemented: daily AI recap, thread inference, day/date
  server queries, person/tag filters, encrypted native sync,
  complete GDPR review or production legal compliance.
  Parents #79/#7/#41/#2/#17 remain OPEN.
