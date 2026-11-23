# ADR 0082 — Combined source and keyword-tag filters over loaded Web timeline

- Date: 2026-11-23
- Status: accepted design; RED/GREEN same-SHA evidence in issue #184
- Related: #79 timeline UX, #7 UX epic, #41 signed Web session,
  ADR 0069–0071 (session boundaries), 0073 (memory pagination),
  0080 (loaded source filtering), 0081 (local-day grouping)
- React Native accessible selectors: https://reactnative.dev/docs/accessibility

## Problem

The Web cloud timeline renders already-loaded keyset pages and supports
a source selection plus the device-local calendar day grouping. The
backend `MemorySummary.Tags` array is genuine signed PostgreSQL
metadata, but users cannot narrow a loaded view by tag. Tags currently
come from deterministic backend keyword matching; they MUST NOT be
advertised as model-generated insights or evidence that a provider
may process additional data.

## Decision

- Derive distinct sorted tag choices from the **whole loaded actor
  page list**, regardless of the currently selected source or tag.
  This prevents an apparent loss of choices when a filter is active,
  and later pages can introduce additional options.
- Use a transient `tagFilter` selection and an accessible `Tutti
  i tag` reset. Filter by exact array membership without substring
  matching or normalization. Combine `sourceFilter AND tagFilter`
  *before* grouping days. The existing source options stay independent.
- Show visible/loaded row count including the two filters and an
  honest empty state. A zero-result filtered view says nothing about
  unrequested pages: `Carica altre note` remains available and
  cursor/row identity/timestamps are unchanged.
- No extra server reads, automatic crawling, tag editing, new DB
  permissions or external connector authorization. The selector is
  rendered only during a signed Web cloud session; native demo does
  not imply signed cloud search.
- All choices and headings are derived in React state. Current-session
  logout, 401 and successful primary erasure clear tag selection
  together with source filter and notes. The existing token+HTTP
  response epoch fence prevents results from a prior account from
  recreating private UI. Selection never reaches URL/storage/logs.

## Evidence and non-goals

Test-first RED introduces a module import that does not yet exist,
and a real Keycloak→JWT→PostgreSQL browser scenario with two explicit
first-party source grants, 11 keyword-work notes and two keyword-friend
notes. The latter only appear after requesting an older page. GREEN
must show source/tag AND behavior, zero headings for no matches,
real day grouping of the two friends, reset and logout wipe.
Exact commit/CI URLs are attached to #184 and must be verified on
the final SHA before closing.

This does not implement global database tag search, provider
OAuth scopes, personalized user-editable tags, semantic AI, multi-
device native synchronization, original per-event timezone or a
GDPR compliance approval. #79/#7/#41/#17/#2 stay open.