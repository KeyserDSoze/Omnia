# ADR 0104 — Social sign-up keyed by e-mail

- Date: 2026-11-26
- Status: accepted (implemented on `main`)
- Supersedes: the "Invite-only social sign-in" decision and the "Social self-registration" rejection of ADR 0101.

## Context

Signing in should be handled with Google and Microsoft. Once a user is created through a social
login, Tao keeps that user and recognizes them by e-mail on the next sign-in. Keycloak is already the
identity broker (realm `tao`, public client `tao-web` with PKCE). The API trusts only the `sub` and
`tenant_id` of its JWTs.

## Decision

- **The user lives in Keycloak, not in a new Tao table.** The first social sign-in creates the user
  in the realm with the provider's e-mail and name. A new table of Tao users would hold personal data
  and need FORCE RLS, sealing, export, erasure and retention for a duplicate of what Keycloak already
  stores. The user's Keycloak id stays the `sub` that actor-scoped data already uses.
- **Open sign-up is the default (`TAO_SOCIAL_SIGNUP=open`).** `keycloak-config` points the `google`
  and `microsoft` identity providers to Keycloak's built-in `first broker login` flow:
  - an e-mail that no user has yet: the user is created and linked, with no extra step;
  - the same provider again: the linked user signs in, so the e-mail is the stable identity;
  - an e-mail that already belongs to a user: Keycloak asks to confirm the link and to prove control
    of that user, by password or by signing in with a provider already linked.
- **`TAO_SOCIAL_SIGNUP=invite`** keeps the previous behavior: the custom flow
  `tao-invite-only-first-login` accepts only users that already exist.
- **`trustEmail` stays off for both providers.** A Microsoft Entra e-mail is not proof of ownership
  (nOAuth), so Tao never merges two accounts on the e-mail alone. Linking a second provider to an
  existing user always needs proof. "Automatically set existing user" is never used.
- **Provider scoping.** `GOOGLE_HOSTED_DOMAIN` restricts Google to one Workspace domain.
  `MICROSOFT_TENANT` sets the Entra tenant (default `organizations`). Both are applied at every start.
- **Signing in is not connecting.** Sign-in grants no Gmail, Calendar or Graph access. Connectors keep
  their own OAuth consent, scopes and revocation.

## Alternatives considered

- **Link by e-mail automatically.** Rejected: a forged or recycled provider e-mail would take over
  the existing account.
- **A `tao_users` table keyed by e-mail.** Rejected, see above: duplicate personal data and a second
  source of truth next to Keycloak.
- **Making the e-mail the Keycloak username.** Not needed: the broker stores the e-mail on the user
  and looks it up. Changing `registrationEmailAsUsername` would touch existing users.

## Consequences

- Anyone with a Google or Microsoft account can create a Tao user on an installation with
  `TAO_SOCIAL_LOGIN` set. Use `invite`, `GOOGLE_HOSTED_DOMAIN` or `MICROSOFT_TENANT` to narrow it.
- Known limitation: someone who first signs in with Microsoft using a victim's e-mail does not take
  over anything, but blocks the victim's later Google sign-in until the victim proves ownership.
  Configuring SMTP in Keycloak lets Keycloak verify the e-mail instead of asking for a password.
- A user created by social login has no Tao password. They can set one from the Keycloak account
  page or keep using the providers.
- Verified by `docker/keycloak/e2e/social-e2e.ps1` with a synthetic OIDC upstream. Real Google and
  Microsoft sign-ins need real app registrations and have not been tested.
