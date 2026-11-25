# ADR 0110 — Azure demo: PaaS, Terraform modules, GitHub Pages front end

- Date: 2026-11-26
- Status: accepted (provisioned from `main`)

## Context

Tao has to be online for a demo without running a server. The front end already lives on GitHub Pages.
The budget is below 130 EUR a month, everything is public (no VNet), PaaS only, Linux, containers kept,
Terraform (not Bicep) split in modules, deployed by GitHub Actions with a client secret.

## Decision

- **Front end: GitHub Pages**, built in cloud mode when the repository variables `TAO_CLOUD_ISSUER` and
  `TAO_CLOUD_API_URL` exist (demo mode otherwise). Same bundle, OIDC with PKCE straight to Keycloak.
- **Back end on one App Service plan** (Linux B2, always on): the API container and a Keycloak container,
  both pulled from Azure Container Registry (Basic) with a user-assigned managed identity (AcrPull).
  A single B2 hosts both: B1 does not have the memory for the JVM next to the API.
- **PostgreSQL Flexible Server** (Burstable B1ms, 32 GB, pgvector allow-listed), public endpoint with the
  Azure-services firewall rule only. Two databases (`tao`, `keycloak`), separate roles: the migration
  identity is the administrator, the runtime and retention roles are created by
  `infra/scripts/bootstrap-db.sh` with the same restrictions as on-prem (no RLS bypass, no ownership).
- **Key Vault** (access policies) holds every secret; the apps read them through Key Vault references
  with their managed identity. The envelope key ring reaches the API as the JSON setting
  `Crypto:KeyRingJson` (ADR 0010 allowed a file only).
- **Monitoring:** Log Analytics and workspace-based Application Insights, both capped at 0.5 GB a day so the
  cost has a ceiling; the API exports through Azure Monitor OpenTelemetry with a processor that strips query
  strings, full URLs and client addresses first; logs below Warning stay local. Web apps send console and
  platform logs, never HTTP logs (they carry query strings). Metric alerts: 5xx, plan CPU and memory,
  database storage and CPU; a monthly budget with notifications. The alert address is a variable.
- **Terraform** in `infra/terraform`: modules `resource-group`, `identity`, `monitoring`, `registry`,
  `postgres`, `keyvault`, `app-service-plan`, `container-web-app` (used twice), `alerts`, `budget`; the
  `demo` environment composes them. State lives in an Azure storage account created by
  `infra/scripts/tf-init.sh`. Images are rolled by push and restart, not by Terraform.
- **Pipeline** `.github/workflows/azure.yml`: init, format, validate, plan, apply, database roles, build and
  push, restart, Keycloak configuration, smoke test from outside. Every step is a script in
  `infra/scripts`, so the same flow runs from a laptop.
- **Two identities, never one.** The service principal that is Owner of the subscription deploys. The Microsoft
  sign-in and Microsoft 365 connector use a separate app registration whose secret lives in Key Vault: a
  leak of the sign-in secret must not hand over the subscription.

## Estimated cost (West Europe, list prices, per month)

App Service B2 ~27 EUR, PostgreSQL B1ms with 32 GB ~17 EUR, Container Registry Basic ~4 EUR, Log Analytics and
Application Insights ~2-5 EUR under the cap, Key Vault under 1 EUR: about 55 EUR, well under the 130 EUR ceiling.

## Alternatives considered

- **Microsoft Entra External ID instead of Keycloak.** Rejected: the API trusts the Keycloak realm
  (`sub` and `tenant_id`) and the first-login and linking rules are Keycloak's; replacing it is a rewrite.
- **Container Apps.** Rejected: the request was an App Service container, and an always-on JVM costs more there.
- **VNet and private endpoints.** Out of scope for a public demo; they would add about 30 EUR.

## Consequences

- The Keycloak administration page is public on its hostname (App Service has no path rules). Mitigation:
  a 32-character random admin password, brute-force lockout on the master realm, and the option to remove the
  admin user after configuration.
- The database is reachable from any Azure service IP: access is by password over TLS. The certificate is
  not verified by the client (`Trust Server Certificate`), because the CA bundle of the runtime image may
  lag Azure's rotation.
- Losing the key ring (Terraform state, Key Vault) loses every sealed note: keep a copy outside Azure.
