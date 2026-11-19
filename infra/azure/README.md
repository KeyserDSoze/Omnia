# Azure PaaS — deployment foundation

**Status:** Bicep architectural starting point, **not deployed or security-reviewed**. Use synthetic data; configure production networking, identity and secrets before real users.

Desired resources:
- ACR for API images
- Azure Container Apps for ASP.NET Core API and future worker
- Azure Database for PostgreSQL Flexible Server with `vector` extension allowlisted
- Azure Storage Blob (future raw audio/documents)
- Azure Service Bus (future outbox/async enrichment)
- Azure Key Vault (future OAuth and service keys)
- Azure Monitor / Log Analytics for telemetry
- Entra External ID (external provisioning) for OIDC SaaS identities

Deployment prerequisites: Azure subscription, resource group, secure secret handling, region/quotas, verified container image in ACR, VNet + private DNS + private endpoints (PostgreSQL, Blob, Vault), Container Apps managed identity with `AcrPull`, CORS allowlisted to `https://keyserdsoze.github.io`, valid HTTPS `Auth__Issuer` / `Auth__Audience` and HTTPS embedding endpoint. Configure PostgreSQL Flexible Server `azure.extensions=vector` before the API's embedded migration runs.

The `main.bicep` foundation declares baseline PaaS resources; additional networking and hardened Container Apps deployment are deliberately blocked until threat-model gates are passed. **It has not been validated through `az deployment`.** Do not treat a template as a successful rollout.


## Recovery-quarantine prerequisite (#141 / ADR 0019)

Before deploying a non-Development Omnia API, an external
operator-controlled, read-only secret/config mount must expose
Recovery__AccessStateFile outside the PostgreSQL backup lifecycle.
Only exact `ready\n` is accepted; a missing or quarantined marker
prevents boot. The current Bicep foundation does not provision a
trustworthy external release authority; no Azure deployment has been
validated. A ready marker DOES NOT authenticate deletion reconciliations.
Do not use with real restored data before the signed anti-rollback
journal, all-worker fencing and recovery review are implemented.
