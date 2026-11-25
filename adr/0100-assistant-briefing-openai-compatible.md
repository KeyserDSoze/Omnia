# ADR 0100 — Assistant briefing through an OpenAI-compatible model

- Date: 2026-11-25
- Status: accepted (implemented on `main`)

## Context

The business case asks for a daily plan with "pre-actions" built from the user's day. A language
model is useful here, but it receives work content (meeting titles, mail previews, chat messages)
that is personal and partly from third parties, and that content can contain prompt injections.
Operators want to choose the model: OpenAI, Azure OpenAI or a local Ollama.

## Decision

- One **OpenAI-compatible** adapter (`POST {endpoint}/chat/completions`, JSON mode) configured by
  `Llm:Endpoint|ApiKey|Model|AuthHeader`; HTTPS required except loopback/private single-label hosts.
  No SDK.
- A **separate consent purpose** `assistant.briefing` (source `assistant`); memory-capture consent
  does not authorize it. The UI discloses the model and host before activation.
- **Minimization:** only open cards (max 25) with kind, title, local time, counterpart name (e-mail
  reduced to its local part), 140-character preview and flags. No ids, keys, links, addresses, tokens.
  No call when nothing is open.
- **Untrusted input:** the system prompt declares card content untrusted; the output must be a JSON
  object whose suggestions reference the provided card refs, otherwise they are dropped.
- **Output is advice:** labeled AI, shown on the matching card, never executed, never stored.
- Model failures map to content-free codes (`model_error`, `model_unreachable`,
  `model_rate_limited`, `invalid_model_output`).

## Consequences

- Quality depends on the configured model; small local models work but write rougher Italian.
- Briefings are not persisted, so there is no history yet; a future "daily memory" will need its
  own consent, retention and erasure design.
