# ADR 0101 — Chat-first home, scripted setup and per-account delegations

- Date: 2026-11-25
- Status: accepted (implemented on `main`)

## Context

The home screen should be a chat. It opens with a question, or with hints from the previous days.
A "magic" button starts a guided configuration that asks which accounts and tools the user has:
Microsoft or Google, Teams, Outlook, Google Workspace, more accounts. The configuration must be fully
deterministic and scripted by us, with no AI. Settings sit at the top right with the user's account,
and sign-in can use Google or Microsoft. Everything is saved in PostgreSQL, so the agent knows which
accounts it can use and what it may do on the user's behalf.

## Decision

- **Deterministic chat home.**
  - `ChatHome` maps a few keywords to fixed actions (`chatScript.ts`): today, plan, note, settings,
    setup. No model is involved.
  - The daily deck and the opt-in AI briefing appear as widgets in the chat. The briefing keeps its
    own consent and labels.
  - Notes, consents, export and erasure move to a separate "Note e privacy" view.
  - Opening hints come from `GET /api/v1/today/recap`, which returns decision counts per day and no
    content.
- **Scripted setup, no AI.**
  - `setupFlow.ts` is a fixed state machine with closed choices. The only free text is the name and
    the labels of extra accounts.
  - It asks about Microsoft and Google accounts (work or personal), their tools, extra accounts
    (up to 10), CRM, working hours and agent mode.
  - Answers become `UserSettings`. Declared tools map to delegations.
  - The server re-validates everything in the domain (`UserSettings.Normalize`).
- **One sealed settings document per actor.**
  - Table `user_settings` (migration 0004), with FORCE RLS and access through `PostgresActorScope`.
  - The document is sealed with `IEnvelopeEncryption` (purpose `user.settings`, AAD = tenant +
    owner + purpose + row id). Only `onboarding_completed_at` and `updated_at` are in clear.
  - The table is in `PostgresRuntimeGuard.PersonalTables`, in the export (decrypted) and in erasure.
- **Delegations enforced on the server.**
  - Capabilities: `calendar.read`, `mail.read`, `tasks.read`, `tasks.complete`, plus `chats.read`
    for Microsoft only.
  - `IConnectorProvider.GetWorkItemsAsync` receives the granted set, and the connectors call only
    those endpoints (data minimization).
  - An account with no read delegation is `paused` and never fetched.
  - Completing at the source requires `tasks.complete`; otherwise the API returns 403
    `delegation_required`.
  - `agentMode: read_only` (the default in the setup) removes every write.
  - Connected accounts that are not declared, and users without settings, keep the previous
    behavior (the provider's full set, filtered by the agent mode).
- **"On behalf of" has three layers.**
  - Tao calls the providers only with the user's delegated OAuth tokens, never app-only permissions.
  - It stays within the delegations stored in the settings.
  - Every write still needs an explicit confirmation for that single action, and is audited.
  - Messages are never sent automatically.
- **Links only to the actor's own accounts.** `SettingsService` drops any `connectedAccountId` that
  does not belong to the same actor and provider.
- **Invite-only social sign-in.**
  - The `keycloak-config` one-shot service runs kcadm idempotently. It enables the `google` and
    `microsoft` identity providers listed in `TAO_SOCIAL_LOGIN` and disables the others.
  - The first-broker-login flow `tao-invite-only-first-login` requires an existing user, link
    confirmation and the Tao password. `trustEmail` is off.
  - The web reads `GET /api/login-options` and passes `kc_idp_hint`. The API is unchanged: it only
    trusts JWTs from the `tao` realm.
  - Signing in with Google does not connect Gmail. Connecting an account remains a separate OAuth
    consent with its own scopes and revocation.

## Alternatives considered

- **LLM-driven onboarding.** Rejected: it is not deterministic, it would send personal answers to a
  model, and it conflicts with the consent-first AI rules.
- **Normalized settings tables.** Rejected for now. Every field is personal, there is no
  cross-user query, and one sealed document keeps the attack surface and RLS policies minimal.
- **Social self-registration.** Rejected: the installation is invite-only, and unverified provider
  e-mails could otherwise take over accounts.
- **Reusing social-login tokens for Graph or Gmail.** Rejected: brokered tokens stay in Keycloak, and
  connectors need their own scopes, consent and revocation.

## Consequences

- Settings are opaque to SQL. Evolving their shape is handled in code, through JSON defaults and
  domain validation.
- `user_settings` has no time-based retention, like `connector_accounts`. It is configuration needed
  to provide the service and lives until the user erases their data.
- Before the setup, the chat makes one API call (settings). After it, the chat makes three
  (settings, recap, assistant).
- Social login needs one more redirect URI per provider. Real Google and Microsoft sign-ins were not
  tested end to end without real app registrations; only the redirects were verified.
