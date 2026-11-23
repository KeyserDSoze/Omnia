# ADR 0083 — Explicit device-local day selection over already loaded memory pages

- Date: 2026-11-23
- Status: implemented slice #185, RED/GREEN exact Actions attached to GitHub issue
- Parent: #79 timeline UX, #7 UX; dependencies #41, #17, #2
- Prior: ADR 0073 (signed memory cursor), ADR 0080 (source), ADR 0081 (local-day grouping), ADR 0082 (keyword-tag)

## Context

The signed Web timeline offers source/tag selectors and local-day headings from keyset pages limited to ten items. It cannot truthfully promise to search every calendar day in the account: its loaded rows form only a prefix of sorted actor-scoped PostgreSQL memory history.

## Decision

- Derive ordered unique `{key,label}` days from **every already-loaded actor-scoped row**, independent of active source/tag/day selections, by reusing `groupTimelineByLocalDay` from ADR 0081; no second date algorithm.
- Use a transient `dayFilter: string | null` and accessible `Tutti i giorni` plus labeled date choices; the key denotes **device-local Gregorian calendar day** for the saved `occurredAt` instant, not UTC slicing and not the original source timezone.
- Apply exact `source AND tag AND day` to the loaded list, then re-group the visible rows to render day headings. No heading remains for a day without matching rows; a missing match is not evidence of absent data on unrequested pages.
- The `Carica altre note` action and its verified JWT/RLS keyset cursor operate independently from filters. New pages may introduce dates or extend the selected date without duplicating headings. There is **no auto-fetch, global DB query, snapshot claim or external connector permission**.
- The selection and day options are ephemeral derivatives of private signed session data. Current-session logout, 401 and successful primary PostgreSQL erasure wipe note rows, source/tag/day selection and their headings. `CloudViewEpoch` and `MemoryApi` already fence delayed prior-account responses. No browser URL, localStorage, AsyncStorage, analytics or persistent caches receive the selected dates.

## Tests, portability and limitations

- TDD RED: pure unit and real Keycloak Chromium fail for missing new day selector; previously existing tests remain green. GREEN verifies browser-authorized synthetic grants/captures, page 10+3, source/tag/day intersection, explicit paging, local-day button and logout wipe. All three final Actions must succeed on one SHA before closing #185.
- Day labels can differ on two devices in different timezones and around midnight/DST, without modifying canonical `occurredAt`. `Intl.DateTimeFormat.formatToParts` behavior is documented at https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Intl/DateTimeFormat/formatToParts.
- Not implemented: original source timezone, thread inference or explicit thread persistence, person filters, full-account day search, native cloud/OIDC/offline sync, generalized backups/provider erasure or GDPR legal validation. Keep #79/#7/#41/#17/#2 OPEN.

## Real OIDC test actor budget (follow-up #185)

The 16th browser scenario is provisioned a third **genuine** disposable Keycloak subject using the same tested S256 PKCE, signed JWT and PostgreSQL actor RLS. Earlier tests legitimately consumed the second subject's request budget and returned HTTP 429. No API rate limit, signed identity validation or data-authorization policy is changed or disabled. Synthetic records remain confined to temporary CI infrastructure.
