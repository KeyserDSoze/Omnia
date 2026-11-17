# ADR 0007 — PostgreSQL FORCE RLS, separate runtime/migration principals, transaction-bound actor

- Date: 2026-11-17
- Status: accepted for M1 privacy hardening; not a production approval
- Parent feature: #30; related #29 consent, #17 identity E2E, #31 delete/export

## Context

The initial API used the local PostgreSQL bootstrap/superuser for both startup schema migrations and runtime personal data queries. While all repository SELECT statements filter tenant+sub, a missing predicate in a future query could leak cross-user content. Superusers and roles with BYPASSRLS always bypass PostgreSQL policies even with FORCE RLS, so adding SQL policies without changing runtime credentials is insufficient.

## Decision

1. `0003_rls_scope.sql` enables and forces RLS for `memory_entries` and `consent_grants`. A single permissive FOR ALL policy requires matching nonempty transaction-local `omnia.tenant_id` and `omnia.owner_sub` for both row visibility and write checks. Unset context defaults to deny.
2. Create a distinct `omnia_app` LOGIN with NOSUPERUSER, NOBYPASSRLS, no DDL/schema ownership and only required DML grants. Local Docker and CI provision a random secret. The privileged schema-owner connection is used only for migrations and grants.
3. `PostgresActorScope` always opens an explicit transaction and uses parameterized `SELECT set_config(..., true)` to bind a signed JWT tenant and subject to the same transaction as the read/update/insert. End of transaction resets scope for connection pooling.
4. `PostgresRuntimeGuard` runs after migrations before HTTP server start and rejects superuser/BYPASSRLS, table-owner-membership and missing FORCE RLS. Distinct migration and runtime connection credentials are mandatory even in Development.
5. Keep existing `WHERE tenant_id=...` plus `owner_sub=...` as defense in depth; RLS must independently protect against omissions. Unit fakes do not replace CI tests against real PostgreSQL with a non-owner role.

## Security and rollout limitations

Custom PostgreSQL GUC variables are freely settable in a session: an attacker with arbitrary SQL execution could alter them. RLS reduces accidental cross-tenant bugs, but parameterization, no arbitrary SQL and defense against injection remain essential. The migration identity can bypass RLS; production should use a separate restricted migration job and secret vault, not long-lived privileged credentials exposed to a public API. Existing persistent Docker volumes require the db-bootstrap one-shot role setup on startup; migrations are versioned and run at API startup. This slice does not implement consent of third parties, data deletion, PostgreSQL encryption, or a fully audited Azure rollout.

## Evidence

`tests/Omnia.Integration.Tests/RlsIsolationTests.cs` plus existing consent/persistence/API tests in disposable PostgreSQL CI. The OIDC workflow exercises signed Keycloak JWT through restricted `omnia_app` into PostgreSQL. CI run link is recorded in issue #30 after verification.
