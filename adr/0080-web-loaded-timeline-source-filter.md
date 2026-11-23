# ADR 0080 — Explicit loaded-page-only source filtering of the Web timeline

- Date: 2026-11-23
- Status: implemented in #182 (RED/GREEN evidence attached to issue)
- Parent: #79 timeline UX, #41 authenticated Web synchronization
- Privacy boundaries: ADR 0069–0071, 0073, 0079

## Problem

The server already provides a \`source\` label for each memory and
keyset pagination \`GET /api/v1/memories/page\`. The authenticated Web
timeline only showed a merged chronological list. To inspect entries
from two sources, users had to scroll manually. Importantly, the
browser only loads ten rows at a time: a visual filter cannot honestly
claim to be a database-wide search.

## Decision

- Preserve the canonical \`source\` in the local display projection.
  React state stores only a selected source string or null (all);
  no URL/query, localStorage, native storage or server write.
- Derive sorted unique filter choices from the **loaded rows**,
  not from guessed provider catalogs or an unbounded API sweep.
  Filtering is exact (case-sensitive to canonical server values),
  read-only, and never changes the underlying memory list/cursor.
- Show a distinct \`Tutte\` action, visible/loaded count, a clear
  loaded-only disclaimer and an honest empty filtered state.
  \`Carica altre note\` remains available even if none of the loaded
  rows matches the selection. Incoming keyset pages can reveal new
  source options and matching rows without removing the selection.
- Source options are shown only in signed-in cloud mode. Logout,
  current-session 401 and erasure immediately clear private notes
  and the selected filter; old HTTP responses remain fenced by
  \`CloudViewEpoch\` and \`MemoryApi\`. A browser hard reload
  discards the filter and requires genuine OIDC again.
- No new authorization or connector permission is implied by a
  displayed source. SQL, RLS, consent gates and API pagination are
  unchanged. Native demo behavior does not pretend to filter
  authenticated cloud memories.

## Evidence and limitations

RED: test-only main commit/Actions in #182, expected missing UI control
and Vitest module. GREEN: pure Vitest contract for unique choices,
stable exact filter and older-page projection; real Keycloak Chromium
creates two first-party grants and 12 synthetic memories through signed
API, verifies explicit older-page loading and filter reset after
reload. All final CI and browser Actions must succeed on one SHA.

This does not fulfill the broader #79 timeline scope (daily groups,
threads, editable tags and platform parity), server-side global
search, full-text search, native cloud sync or GDPR legal review.

References:
- React Native Pressable: https://reactnative.dev/docs/pressable
- React Native accessibility: https://reactnative.dev/docs/accessibility
