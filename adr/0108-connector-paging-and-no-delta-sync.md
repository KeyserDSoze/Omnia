# ADR 0108 — Connectors: bounded paging, names from the same fetch, no delta sync yet

- Date: 2026-11-26
- Status: accepted (implemented on `main`); delta, webhooks and an address book are deliberately open

## Context

Epic #222 listed delta sync, webhooks and contacts as the remaining work. Tao reads the providers live
and stores no provider content (ADR 0099). At the same time the readers took only the first page of
every list: 25 Outlook messages, 20 Gmail ids, 50 events. With a deck that now looks back two days
(ADR 0106) a busy inbox could silently hide unread mail.

## Decision

- **Follow the pagination, bounded.** Graph `@odata.nextLink` and Google `nextPageToken`, at most 3
  pages per list (150 items, 40 Gmail messages). A provider that never stops paging cannot make Tao
  fetch without bound.
- **Never send the token elsewhere.** A Graph next link is followed only on `https://graph.microsoft.com`;
  any other host ends the paging. Google page tokens are appended to the same request URL.
- **Names from the same fetch.** An organizer, attendee or sender known by address and name in one read
  names the cards that carry only the address. It lives for one request, uses no new permission and
  stores nothing. This is what Tao does for "contacts" for now.
- **No delta sync, webhooks or address book yet.**
  - Delta (Graph delta queries, Gmail history, Calendar sync tokens) only pays off with a local copy of
    the items, which is what ADR 0099 avoids: a cache would be one more table of provider content to
    seal, export, erase and expire. Tokens alone, without a copy, give nothing to show.
  - Webhooks need the cache above plus a public endpoint and renewal of subscriptions.
  - A real address book needs new scopes (`Contacts.Read`, `contacts.readonly`, a Google restricted
    scope under verification) and a re-consent of every connected account.

## Alternatives considered

- **Raise the page size.** Rejected: providers cap it anyway, and a bigger first page is still bounded
  silently.
- **Cache provider items sealed in PostgreSQL to enable delta.** Not now. If the daily load time becomes
  a real problem, this is the next step and needs its own ADR on retention and consent.

## Consequences

- A very busy day can still exceed 150 items of one list; the oldest are the ones left out.
- Opening the deck costs a few more requests than before, only when a list really has more pages.
