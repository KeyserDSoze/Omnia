# ADR 0106 — Deck: undo, keyboard shortcuts and a queue for tomorrow

- Date: 2026-11-26
- Status: accepted (implemented on `main`)

## Context

The daily deck (ADR 0099) stores one triage row per card (`triage_decisions`, key and decision, no
provider content). A wrong swipe could not be taken back, the cards could not be sorted from the
keyboard, and "later" meant only two hours. A card pushed to tomorrow would also have vanished: the
deck fetched just the previous evening and today.

## Decision

- **Undo clears the decision.** `DELETE /api/v1/today/decisions?key=` removes the actor's row and the
  card returns undecided. A row that already completed the task at the provider
  (`applied_to_source`) is kept and the API answers 409 `applied_to_source`: Tao never pretends to
  revert a write on another system. The deletion runs through `PostgresActorScope` (FORCE RLS), so
  another user's key is simply 404. No decision history is added: undo is "as if undecided".
- **The web app undoes only this session's last decisions** (a stack of ten), with an *Annulla* bar
  and the `Z` key.
- **Keyboard shortcuts act only inside the deck.** With the card focused (`tabindex=0`) and not in a
  text field: right arrow done, left not done, down later, up tomorrow, Del/Backspace dismiss, `Z`
  undo. Any modifier key is left to the browser.
- **Tomorrow is a snooze to the next local 08:00.** The client computes the minutes (DST-safe) and
  sends `snoozeMinutes`, now 15–2880. The window the API accepts grows from 72 to 96 hours and the
  deck asks for two previous days, so mail queued for tomorrow is still fetchable then. Undecided
  mail and chats older than the previous evening stay hidden, as before; only a queued one returns.
- **Time stays server-side where it matters.** The server computes the snooze end from its own clock.

## Alternatives considered

- **A decision history table.** Rejected: more personal data to export, erase and expire, for a
  feature that needs only the current state.
- **Reverting the provider write on undo.** Rejected: completing a task is a confirmed, audited action
  on another system; reopening it would be a second write the user did not ask for.
- **Global keyboard shortcuts.** Rejected: they would hijack the page scroll and the chat composer.

## Consequences

- The deck request may return up to two more days of mail and events; the client filters them.
- The recap counts (chat opening hints) lose an undone decision, as it no longer exists.
