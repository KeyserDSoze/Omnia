# ADR 0003 — Cloud/local parity, fail-closed OIDC and consent first
Date: 2026-11-17. Status: accepted as target architecture; only selected adapters coded.

## Decision
PostgreSQL in Azure and Docker, with DI for embedding providers (None/Ollama/Azure OpenAI). Future ports pair Blob↔MinIO, Service Bus↔RabbitMQ, Key Vault↔local dev secrets, Entra External ID↔Keycloak. Identity must be a signed JWT with one tenant_id and sub; do not let requests pick their tenant. Untrusted MCP/connectors receive restricted per-capability permissions and require explicit consent and audit before delegated actions.

## Tradeoffs
No universal access/phone call recording promise; capabilities depend on OS, provider policies and law. Keycloak development realm does not constitute end-to-end tested production authentication.
