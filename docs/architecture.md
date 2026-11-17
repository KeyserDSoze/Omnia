# Omnia — architettura v0.1 (2026-11-17)

## Scelta architetturale

Monorepo ispirato a SoftwareFactory, con confini espliciti `domain → application → infrastructure → api`, test indipendenti, documentazione e ADR. Modular monolith **prima** dei microservizi. Su Azure un Container App .NET 10 e servizi PaaS. Expo Router ha un bundle statico per GitHub Pages e build native Android/iOS: il backend NON viene ospitato su Pages.

```text
Expo React Native (iOS, Android, GitHub Pages /Omnia/)
     | OAuth Authorization Code + PKCE [not yet implemented in UI]
     v
Omnia.Api ASP.NET Core .NET 10 (Bearer JWT/JWKS OIDC)
     | DI: IMemoryCatalog
     v
Omnia.Application (catalog, provenance, classification)
     | IMemoryRepository    | IEmbeddingProvider
     v                      v
Omnia.Infrastructure     None | Ollama | Azure OpenAI
     |
PostgreSQL 17+ pgvector (same engine dev/test/production)
```

## Bounded contexts (target)

1. **Identity & Tenancy** — OIDC provider, tenant, subject, OAuth consent registry, grant expiry and revocation.
2. **Connections** — connector manifests, OAuth tokens in managed secrets, MCP transport, capability discovery, per-source synchronization checkpoints and rate limits.
3. **Capture & Provenance** — microphone/import/webhook ingest; immutable source events, original payload in immutable object store (planned for binary), timestamps, source IDs, checksums.
4. **Memory** — canonical event schema, user taxonomy, tags, summaries, embeddings, thread stitching, temporal context and evidence links.
5. **Intelligence** — retrieval with references, personal trend analysis, observed vs inferred distinctions and correction workflow. No diagnosis or unquestionable personality verdicts.
6. **Agency** — plan → preview → approve → execute → audit; no write-capability grant by default.
7. **SaaS** — billing, quotas, residency, retention, encrypted backup, export and deletion: **not implemented**.

Initially only a thin slice of Capture/Memory is coded. Do not present the other contexts as implemented.

## Canonical event contract

```json
{
  "id": "uuid",
  "schemaVersion": 1,
  "tenantId": "trusted-jwt-claim",
  "subjectId": "trusted-jwt-sub",
  "source": "crm",
  "sourceItemId": "event-123",
  "occurredAt": "2026-11-17T10:00:00Z",
  "receivedAt": "server-generated",
  "contentType": "application/json",
  "raw": { "storage": "inline-text-v0.1", "value": "{}" },
  "normalized": { "text": "..." },
  "summary": "...",
  "tags": ["lavoro"],
  "embedding": { "dimension": 768, "model": "nomic-embed-text", "status": "ready-or-pending" },
  "threadId": null,
  "consentId": null,
  "provenance": { "checksumSha256": "planned", "connectorVersion": "planned" }
}
```

Current implementation uses `memory_entries` with raw text, JSONB normalized form, summary, text[] tags and nullable vector(768). The sample includes *future fields* explicitly marked as planned. Import is limited to text/plain, application/json and XML (as raw text). MIME allowlist, 64-KiB raw limit and PostgreSQL tenant/user filters apply. Idempotency key: `(tenant_id, owner_sub, source, external_id)`. Domain class validates, application classifies and invokes an injectable embedding provider, infrastructure persists via Npgsql parametrized SQL and a versioned migration protected by advisory transaction lock.

**Important:** keyword rules are placeholders, not generative summaries, inferred emotion or AI coaching. Embedding `null` is not an invented vector: the data awaits an indexing pipeline. No data is sent to AI when `Embeddings:Provider=none`. For `ollama` and `azure`, content is sent to the configured embedding endpoint with explicit operator setup.

## Sync + agent future shape

A connector implements `DiscoverCapabilities → ConnectWithConsent → Pull/Subscribe → Normalize → Emit`. Each event carries idempotency key, occurrence time, source, origin metadata and optional thread references. Do not assume that all integrations grant the same access: WhatsApp Business APIs, Microsoft Graph, devices, telephony, and MCP have distinct auth and permissions. MCP servers should run in isolation behind capability allowlists, human approval and rate limits (not present yet). Use inbox/outbox and durable queue for future async enrichment, retries, dead-letter handling, poison input isolation and deletion propagation.

Actions are **read-only by default**. For sending messages, require user-approved per-recipient scopes; never silently send as the user. Suggested drafts must expose underlying evidence and distinguish observed facts from AI inference.

## Azure PaaS ↔ local open source by DI (target matrix)

| Port / capability | Azure PaaS | Docker/test |
|---|---|---|
| IMemoryRepository | Azure Database for PostgreSQL Flexible Server + pgvector | pgvector/pgvector:pg17 |
| IEmbeddingProvider | Azure OpenAI embeddings | Ollama nomic-embed-text |
| IObjectStore (planned) | Azure Blob Storage | MinIO |
| IEventBus (planned) | Azure Service Bus | RabbitMQ |
| Identity/OIDC | Entra External ID (planned) | Keycloak |
| ISecretStore (planned) | Azure Key Vault | OpenBao (open source, planned) |
| Observability | Azure Monitor / Application Insights (planned) | OpenTelemetry Collector + Jaeger (planned) |\n| ICache (optional) | Azure Managed Redis (planned) | Valkey OSS (planned) |\n| Realtime notifications (optional) | Azure Web PubSub (planned) | Self-hosted SignalR (planned) |
| Images/runtime | ACR + Azure Container Apps | Docker Compose |

Only PostgreSQL, JWT/OIDC validation and embedding adapters are actually implemented. All other mappings are design decisions, not production-ready integrations.

## Architecture gates

- No unauthenticated data endpoints. Identity requires **exactly one non-empty `sub` and `tenant_id`** in valid signed OIDC bearer claims. Public `/health` is not readiness.
- Mobile/web UI is disconnected from the secured API until a real native/web PKCE flow exists; it must never fake cloud persistence.
- Local audio capture requires explicit microphone permission and records locally only. Phone call capture remains platform/restriction dependent and is **not implemented**.
- Production requires private network, TLS, issuer, least privilege, tenant isolation tests, signed consent, content retention, deletion/export, rate limiting, threat model, secret rotation and observability before accepting real personal data.

> **Parità non significa disponibilità corrente:** OpenBao/Valkey/SignalR sono contratti di progetto, non servizi avviati dal Docker Compose attuale. Ogni nuovo port richiede test contrattuali contro la variante OSS e staging Azure, prima di essere dichiarato supportato.

## Consent-first ingest (vertical slice of #29)

`ConsentGrant` è un'entità di dominio immutabile con `tenantId`, `ownerSubject`, `source`, `purpose`, `policyVersion`, `grantedAt`, `expiresAt` e `revokedAt`. `ConsentService` in Application ammette solo scopo `memory.capture` con acknowledgement esplicito e TTL massimo 90 giorni; `IConsentGuard` è controllato prima di `IEmbeddingProvider`. PostgreSQL usa migrazione `0002_consent_grants.sql`, index unico sul grant non revocato per scope, transaction advisory lock per re-grant; `PostgresMemoryRepository` fa un ulteriore controllo atomico `SELECT ... FOR SHARE` prima dell'UPSERT raw. La revoca dell'accesso e la persistenza non possono attraversarsi senza lock transaction.

Il grant non conferisce automaticamente il permesso di registrare terzi, accedere a CRM/WhatsApp, effettuare profilazione oppure eseguire strumenti MCP. L'accesso ai dati storici rimane disponibile finché feature separate di export, retention e cancellazione non saranno implementate. Contratto: [docs/consent-api.md](consent-api.md). Release readiness: parziale, non per dati reali.

## API per-identity throttling (first slice #37)

ASP.NET Core built-in rate-limiter middleware is placed **after JWT authentication** and before authorization. Both v1 API groups require a fixed-window per authenticated tenant and subject partition (120 req/min default) or fallback peer IP for unauthorized requests. Endpoint rejects with 429 and no waiting queue. The limiter is **instance-local**: it does not establish a cluster-wide SaaS quota and is not a replacement for the Azure network edge WAF, per-tenant budget, or durable metering (#102).
