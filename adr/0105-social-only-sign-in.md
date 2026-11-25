# ADR 0105 — Social-only sign-in, no Tao password

- Date: 2026-11-26
- Status: accepted (implemented on `main`)
- Amends: ADR 0104 (removes the `invite` mode and the password proof) and ADR 0101.

## Context

Tao accounts must exist only through Google or Microsoft. The old email-and-password login is gone
for good. The provider apps' client secrets should be what vouches for the incoming tokens.

## Decision

- **No password form.** When `TAO_SOCIAL_LOGIN` lists a provider, `keycloak-config` binds the realm
  browser flow `tao-social-only-browser`, which has a single step: the Identity Provider Redirector.
  The web app always sends `kc_idp_hint`; a request without a hint reaches no login form. Direct
  grants were already disabled, registration and password reset stay closed.
- **The web app shows only the provider buttons.** The "sign in with email and password" link is
  removed in the 21 languages. The header and settings sign-in actions use the only provider, or
  bring the user back to the buttons when there are two.
- **`TAO_SOCIAL_SIGNUP` is removed.** The `invite` mode needed a Tao password. Access is narrowed
  with `GOOGLE_HOSTED_DOMAIN` and `MICROSOFT_TENANT` instead.
- **Linking a second provider** still needs proof: re-authentication through the provider already
  linked, or e-mail verification when SMTP is configured in Keycloak.
- **What the client secrets do.** Both apps are confidential clients. Keycloak redeems the
  authorization code at the provider's token endpoint with the client secret, so a token is accepted
  only if it comes from that exchange. Google ID tokens are also checked against Google's published
  signing keys (`validateSignature`, issuer and audience). Microsoft's identity comes from Microsoft
  Graph `/me` with the access token obtained the same way. The secret does not validate a token's
  signature; the API still trusts only JWTs signed by the `tao` realm.
- **Fallback.** With no provider configured the stock Keycloak login stays, so a fresh install and
  the CI stacks that have no Google or Microsoft app are not locked out.

## Consequences

- A user cannot sign in without a Google or Microsoft account; losing it means losing access.
- A password credential that an old user already has is unused and cannot be used to sign in.
- The Keycloak account console has no login form either, so settings no longer link to it when a
  provider is configured. Profile and linked identities are managed by the providers.
- Users created before this change keep their Keycloak id, so their data stays.
