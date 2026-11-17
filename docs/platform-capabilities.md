# Capability boundaries and external integration research

Updated 2026-11-17. **Verify platform policies again when implementing**: OS/API terms and regional law change.

## Telephony iOS and Android

- Standard RN/Expo microphone permission enables local memos, **not** recording both sides of any cellular call. There is no universal public third-party API to intercept native phone/FaceTime audio. Distinguish CallKit/VoIP *managed by own app* from OS Phone.app calls.
- For iOS evaluate Apple CallKit and supported system-export/share flows separately. For Android evaluate device/region-specific restrictions and store policy, no hidden capture. A legal review is required for calls with multiple participants.
- Alternative legitimate MVP paths: manually import user-provided recording; integrate official meetings/telephony providers where recording grants and attendee notices exist; direct microphone memo intentionally started by user.

References:
- https://developer.apple.com/documentation/callkit
- https://developer.apple.com/forums/thread/841340

## WhatsApp

- Meta WhatsApp **Business Platform** official API is for approved business accounts/numbers and workflows. It is **not** authorization to read any personal WhatsApp inbox or clone the account. Respect messaging windows/templates and providers' data use terms.
- Reference: https://developers.facebook.com/documentation/business-messaging/whatsapp/overview

## Microsoft Graph

- Outlook email, calendar and contacts support delta queries/change notifications (resource-specific support and restrictions). Use delegated least-privilege OAuth and incremental checkpoints, respect subscriptions and throttling, handle permission revocation and deletion.
- References:
  - https://learn.microsoft.com/en-us/graph/delta-query-overview
  - https://learn.microsoft.com/en-us/graph/outlook-change-notifications-overview

## MCP

- Support current specified MCP transport/authorization with origin and audience checks, no token passthrough, SSRF/tool sandbox, tool output as *untrusted data*. Per-call user approval for write-side effects, cancellation and policy.
- References:
  - https://modelcontextprotocol.io/specification
  - https://cheatsheetseries.owasp.org/cheatsheets/MCP_Security_Cheat_Sheet.html

## Azure pgvector and Expo Pages

- Azure PostgreSQL Flexible Server requires extension allowlisting/configuration for `vector`; validate DB/PaaS version before rollout. Bicep baseline isn't a live deployment.
- Expo Router supports static rendering and GitHub Pages base path; do not use Pages for an API or secret storage.
- References:
  - https://learn.microsoft.com/en-us/azure/postgresql/extensions/how-to-use-pgvector
  - https://docs.expo.dev/router/web/static-rendering/

## Privacy and human agency

- Treat recordings, contact graphs, communications and inferred behavior as high-risk personal/third-party data. The GDPR applies to profiling; legal basis, transparency, rights, residency and data processor contracts need expert review before launch. AI reflection is **not** objective behavioral judgment, diagnosis or a medical recommendation.
- References:
  - https://commission.europa.eu/law/law-topic/data-protection/information-individuals_en
  - https://eur-lex.europa.eu/eli/reg/2016/679/oj/eng/
