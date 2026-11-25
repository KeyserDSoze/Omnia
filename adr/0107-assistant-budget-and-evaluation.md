# ADR 0107 — Assistant: daily budget, redaction and a repeatable evaluation

- Date: 2026-11-26
- Status: accepted (implemented on `main`)

## Context

The daily plan (ADR 0100) calls a model the operator pays for, with no limit, an unbounded answer and
no way to tell whether a given model is good enough. An evaluation also showed a privacy gap: card
titles and previews were sent as they were, so a link, an e-mail address or an IBAN quoted in a mail
reached the model although the documentation promised otherwise.

## Decision

- **Per-user daily budget.** Calls (default 20) and tokens (default 200000) per local day of the user,
  and a maximum answer size (`max_tokens`, default 700). They are checked only when a model call is
  really about to happen, and the answer carries `usage` so the UI shows "Plans today: 2 of 20".
  Beyond the limit the API answers 429 `assistant_budget_exceeded` without calling the model.
- **Counters in PostgreSQL, not in memory.** `assistant_usage` (migration 0006) holds day, calls and
  tokens only. It is a personal table: FORCE RLS, in the export, erased with the user, purged after 35
  days by the bounded retention function. The call is reserved with one atomic upsert, so concurrent
  requests cannot overshoot the limit.
- **Real token counts when the provider reports them** (`usage`), otherwise about four characters per
  token. A failed call keeps its reserved call and adds no tokens.
- **Redaction before sending.** Links, e-mail addresses and IBANs in titles, previews and counterpart
  names become `[link]`, `[email]`, `[iban]`.
- **Nothing risky comes back.** Links and addresses are removed from the summary and a suggestion
  containing one is dropped, so an injected instruction cannot place a phishing link or a recipient
  in the plan.
- **Repeatable evaluation.** Fixed scenarios and eight checks (what leaves Tao, what comes back) are
  tested against scripted good and bad models in the normal suite, and run against the operator's
  real model with `TAO_EVAL_LLM_*` (skipped otherwise). Minimization and the output guard are enforced
  by Tao and must always pass; the rest measures the model.

## Alternatives considered

- **In-memory counters.** Rejected: a restart resets the budget, and the counters would not follow the
  user's export and erasure.
- **A global installation cap.** Not done: RLS keeps one user from seeing another's rows, and the
  provider's own spending limit is the right tool for that.
- **Scoring answers with a second model.** Rejected: it sends the content to a model again and makes
  the evaluation non-deterministic and paid.

## Consequences

- The first request of the day may be refused when the daily token budget is spent by one large plan.
- `IChatModel.CompleteJsonAsync` now takes the answer size and returns the provider's usage.
- A small local model passes the bar on ordinary scenarios but turns verbose with many cards: the
  evaluation shows it, which is the point.
