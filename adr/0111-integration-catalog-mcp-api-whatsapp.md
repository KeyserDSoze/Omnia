# ADR 0111 — One integration model: built-in, MCP and direct API connections

- Date: 2026-11-26
- Status: proposed (design and backlog only; no code yet)

## Context

Tao must let the user connect to anything with very little effort, from the chat or from the
settings: "what can I connect?" must answer with the list. Three families exist:

1. **Built-in integrations** shipped with the platform (Microsoft Graph, Google Workspace, WhatsApp).
2. **MCP servers**, with or without authentication (OAuth, or a key in a header).
3. **Direct API integrations**, where an API key or OAuth is enough.

OpenClaw (MIT, TypeScript, <https://github.com/openclaw/openclaw>, docs at <https://docs.openclaw.ai>)
already solved the same breadth. Studied on 2026-11-26:

| Area | What OpenClaw does | Taken by Tao |
|---|---|---|
| Channels | A gateway owns each messaging session (WhatsApp Web through Baileys, Telegram, Slack, Teams, Signal, Discord, iMessage). Install per channel as a plugin; WhatsApp login is a QR scan ("linked device"). | The idea of a *channel* as a first-class kind; QR pairing for WhatsApp; one isolated session per user. |
| Access control | Unknown senders get a one-hour pairing code and are not processed until approved; allowlists for DMs and groups; groups need a mention; self-chat rules; reusable sender groups. | Pairing code and allowlist for every inbound channel; deny by default. |
| MCP | `Settings → MCP → Add server` (name, transport Streamable HTTP / SSE / stdio, URL), then a *probe* that connects and lists tools. OAuth by sign-in link in the same chat; `include`/`exclude` tool filters; per-session tool denial; the last catalog is kept while a server is down; exponential backoff; server instructions capped at 2000 characters; tool errors are returned to the agent; per-tool approvals. | Add-then-probe flow, sign-in link in the chat, tool filter, cached catalog with backoff, error pass-through, capped instructions, per-tool grants. |
| Plugins and skills | Capabilities ship as plugins/skills from a registry (ClawHub). | A curated catalog file; no third-party code execution. |
| Security | One trust boundary per gateway; it is *not* a multi-tenant boundary. Inbound content is untrusted and wrapped; secrets on disk; `security audit`. | Per-user isolation instead of a shared gateway; untrusted wrapping already in ADR 0107. |
| Not taken | Tools run on the host with shell access; stdio MCP servers start local processes; Baileys is an unofficial protocol. | Rejected for the hosted service (see below). |

## Decision

1. **Three kinds, one `Connection`.** `built_in`, `mcp` and `api` all become a per-user connection with
   the same lifecycle: catalog → connect → test → choose capabilities → use → disconnect. Direction is
   explicit: `source` (Tao reads signals for the deck), `tool` (the assistant may call it, with the AI
   consent) or `channel` (the user talks to Tao from there, like WhatsApp or Telegram).
2. **A catalog, not hard-coded screens.** A versioned file in the repository lists every integration
   Tao knows (id, kind, direction, auth, capabilities, risks, how to connect). The API serves it at
   `GET /api/v1/integrations/catalog`. The chat answers "what can I connect?" deterministically from
   it (no model, like ADR 0101): *already integrated*, *MCP servers*, *direct APIs*.
3. **Connect in three steps at most.** (1) pick from the catalog or paste an address; Tao detects what
   it is (an MCP endpoint answers `initialize`; a 401 with `WWW-Authenticate` means OAuth; an OpenAPI
   document means a direct API); (2) authorise with the OAuth popup (the flow of
   `docs/connectors.md`), a pasted key, or a QR code; (3) pick what it may do, read-only by default,
   then run a test call that proves it works.
4. **One policy layer for every call**, whatever the kind:
   - capabilities extend the delegations of ADR 0101 (`<integration>.<verb>`); the integration is
     called only for delegated capabilities (data minimisation);
   - reading is the default; every write needs the explicit confirmation of that single action and
     leaves an audit row (as `connector_actions`);
   - results are untrusted input: bounded in size, wrapped, never executed or sent on automatically;
   - the assistant can use a `tool` connection only with a dedicated consent (`assistant.tools`),
     and per-tool include/exclude filters apply before the model sees the catalog.
5. **Outbound guard for addresses the user types** (MCP URL, API base URL): HTTPS only, resolve then
   pin the address, refuse private, loopback, link-local and cloud-metadata ranges, no redirect to
   another host, short timeouts, response size caps, per-tenant rate limits. Without it a pasted URL
   reaches the Azure managed-identity endpoint. This guard is a prerequisite of every user-supplied
   address.
6. **No stdio MCP servers in the hosted service.** They start processes on the host. They may be
   offered later on-prem only, behind an administrator switch.
7. **Secrets** (OAuth tokens, API keys, header values, WhatsApp session) are sealed with
   `IEnvelopeEncryption` (AAD tenant + owner + purpose + row id), live only on the server, never in
   logs or in the export. MCP OAuth follows the MCP authorization specification (protected resource
   metadata, PKCE, dynamic client registration when offered) and reuses `/connections/callback`.
8. **WhatsApp has two modes, decided by risk.**
   - *Official WhatsApp Business Cloud API on a dedicated Tao number* as a **channel**: the user
     writes to Tao, the first message needs a pairing code approved in the app, then a sender
     allowlist. Default. Needs a Meta business account and a number.
   - *Linked device (Baileys-like) on the user's own number* as a **source**: experimental, opt-in
     per user, **on-prem only, never in the hosted demo**, one isolated sidecar per user, session
     credentials sealed. It reads only the chats the user lists plus the self-chat, never sends
     without a per-message confirmation, and keeps summaries and decisions, not raw conversations.
     The user sees the warning that the protocol is unofficial and the number can be suspended.
     Messages of other people are third-party personal data: a legal review (WhatsApp terms,
     GDPR roles) comes before shipping.
9. **Tao as an MCP server** (R3): read tools such as today's deck and note search, authorised through
   the Keycloak realm, so OpenClaw or any MCP client can use Tao.

## Alternatives considered

- **Embed OpenClaw as the integration backend.** Rejected: one gateway is one trust boundary, so
  multi-user would need one process per user; it runs tools with host access; its WhatsApp driver is
  unofficial. Reusing ideas and, where licensing allows, small libraries is fine.
- **MCP only.** Rejected: the deck needs deterministic cards mapped from Graph and Google fields,
  which a generic tool list cannot give; MCP tools stay assistant tools.
- **Direct API only through hand-written connectors.** Rejected: slow to extend. A declarative
  manifest (base URL, auth, operations imported from OpenAPI) lets a new API be added without code.

## Consequences

- The catalog becomes the single source for the chat answer, the settings page, the docs and the
  "what is integrated" statement; keep it tested against the code (an integration cannot be marked
  `available` without a registered implementation).
- New personal tables (`integration_connections`, `integration_actions`) follow the repository rules:
  FORCE RLS, `PostgresActorScope`, entry in `PostgresRuntimeGuard.PersonalTables`, export, erasure,
  retention, tests on a real PostgreSQL including cross-actor access.
- The first slice is only the catalog and the chat answer: it adds no outbound call and no secret.
- Tracking: epic and issues listed in `docs/backlog.md`; study notes and the user-facing catalog in
  `docs/integrations.md`.
