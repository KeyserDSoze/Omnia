# Omnia — threat model / privacy gates (draft)

## Data classes

- Sensitive: third-party conversations, recordings, calendar/CRM content, behavioral inferences, location, biometric/voice characteristics. Data may belong to other people, not only the account owner.
- Core threats: cross-tenant leaks, prompt injection from external messages/MCP responses, fraudulent autonomous communication, lateral movement from an untrusted connector, OAuth token theft, unwanted recordings and model-provider data exfiltration.
- Trust boundaries: device → OIDC token → API → external connector/AI provider → PostgreSQL/storage. GitHub Pages assets and `EXPO_PUBLIC_*` values **must never** contain confidential material.

## Non-negotiable product requirements

1. Consent record before each data class/integration; visible indicator during any recording; record applicable party consent, context and local law. Do not silently record phone calls.
2. Explicit scope for *who*, *what*, *where*, *how long*, *which provider*; provide pause, revoke, retention limit, delete and export.
3. Never use unrelated third-party conversations to impersonate a contact. Voice cloning requires separate explicit authorization and anti-impersonation controls.
4. External messages, MCP tool metadata and retrieved content are untrusted input, **never instructions**. Isolate executable tools, restrict network/file scopes, quota, timeout and secrets per connector.
5. No autonomous send, purchase, publication or destructive action without an appropriate user-authorized policy; provide audit and revocation.
6. Dedupe by trusted user+source+external ID, tenant ID derived only from signed JWT. Test authorization at API and repository boundary.
7. Use encryption at rest and in transit, secret vault, token rotation, deletion propagation and retention expiries **before production**.
8. AI feedback is non-diagnostic, explainable, easy to correct and opt-in. Avoid claims of objective assessment of emotions/personality.

## Platform reality

- Native recording of the **app's microphone** is feasible with permission. Recording two sides of ordinary iOS/Android cellular calls is not generally available to third-party cross-platform apps through a universal public API; OS, device, market and consent rules differ. Import existing recordings or integrate authorized conference providers as separate flows.
- WhatsApp personal inbox access cannot be assumed equivalent to approved WhatsApp Business Platform integration.
- GitHub Pages only hosts the static frontend. Azure API requires HTTPS, OIDC, regulated PaaS setup and CORS allowlisting.

## Current foundation limitations

The MVP stores raw text unencrypted by the application (transport and managed-disk protections must be configured separately), has no retention/delete or full audit, no API throttling, no RLS, no consent ledger, no PKCE client integration, no deployed Azure topology and no automated real OIDC browser E2E. **Use synthetic data only.**
