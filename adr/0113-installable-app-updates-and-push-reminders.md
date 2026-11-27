# ADR 0113 — Installable app, versions and update prompt, reminders as Web Push

- Date: 2026-11-27
- Status: accepted and implemented (app 0.2.0 and 0.3.0)

## Context

Tao is a web app on GitHub Pages (and behind the on-prem gateway). Users want it on the phone and the desktop like an
app, to hear about new versions (and not be bothered by them if they prefer), and to be reminded of things even when
Tao is closed. A reminder that only works while the page is open is not a reminder.

## Decision

**Installable app (PWA).** A web app manifest, icons and a service worker (`public/sw.js`). The worker only keeps the
app shell and hashed static files: it never touches `/api`, `/auth`, `/.well-known`, `connections/`, `version.json` or
any request that is not a GET to the same origin, so personal data is never cached. Offline, the shell opens; the
data does not (the API answers with `no-store`).

**Versions and updates.** `release-notes.json` is the single source of the notes, newest first, and its first entry
must equal the app version (a test enforces it). The build writes `version.json` (version, build id, notes of the
last releases) and stamps the worker. The build id is the hash of the entry bundle, read by the running page from its
own script tag: it changes exactly when the web app changes, so a back-end-only commit never prompts anybody.

- Default: the page asks, showing what changed (in the user's language, English as the fallback). Choices: update
  now, later (until the next session), or "don't ask again".
- "Don't ask again" stores a preference; from then on the new worker takes over in the background and the next start is
  the new version. It can be undone in Settings.
- `version.json` is validated strictly before it is rendered: it is data, never markup.

**Reminders and push (Web Push, RFC 8030/8291/8292), implemented in Tao without a library.**

- A reminder is a text with a repeat (once, daily, weekdays), a local time and the user's time zone; the next run is
  computed with the zone's daylight saving rules. At most 50 per user.
- Each device subscribes with its own browser; the server keeps at most 5 per user. The address of a subscription is
  a capability that the server will POST to, so only the push services of the browsers are accepted (FCM, Mozilla,
  Windows, Apple), over HTTPS, through the SSRF-guarded outbound client of ADR 0111.
- Notifications are opt-in with a dedicated consent (`notifications.push`) on top of the browser permission. Without an
  active consent nothing is sent, and turning notifications off revokes it.
- `reminders` and `push_subscriptions` are actor-scoped with FORCE RLS; the text and the subscription (address and
  keys) are sealed with the envelope encryption (AAD: tenant, owner, purpose, row). They are in the runtime guard, the
  export (the address is reduced to its host, keys never leave), the erasure and the retention.
- The payload is end-to-end encrypted to the browser (RFC 8291); the push service only sees metadata. The text is
  bounded and shown as plain text; clicking a notification always opens the app, never an address from the payload.
- The server's VAPID key is generated on first use and stored sealed in `push_settings`, so every replica (cloud or
  on-prem) shares it without extra configuration. Replacing it invalidates the subscriptions.
- Dispatch: every replica polls every 30 seconds. The claim is one `SECURITY DEFINER` function
  (`tao_claim_due_reminders`) executable only by the restricted worker role, which returns identifiers and nothing
  else and leases each reminder for 5 minutes (`FOR UPDATE SKIP LOCKED`); the content is then read and decrypted in
  the actor's own scope. A reminder more than 6 hours late is skipped, not sent. Finished one-off reminders and
  subscriptions that keep failing are purged by the retention job.

## Consequences

- On iPhone and iPad, notifications work only when the app has been added to the Home Screen (iOS 16.4 or later).
- Push needs a secure context: on-prem installs need HTTPS (localhost works for trials).
- The delivery time depends on the push service; reminders are not guaranteed to the second.
- Changing the key ring without re-sealing makes the VAPID key unreadable, like every other sealed value.

## Alternatives considered

- A Web Push library: one more dependency to lock, audit and keep in the Docker build for about 150 lines of
  well-specified cryptography, verified here against the RFC 8291 example byte for byte.
- A global dispatcher role with BYPASSRLS: refused; the narrow claim function exposes identifiers only.
- Reminders in the browser only (timers, Notification Triggers): they do not fire when the app is closed.
