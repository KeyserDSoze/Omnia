# ADR 0005 — Source-scoped consent gate and transactional PostgreSQL enforcement

- Date: 2026-11-17
- Status: accepted **for foundation slice**, not GDPR production approval
- Related: #29, #31, #32, #39, #41 and ADR 0004

## Context

The v0.1 memory API accepted authorized OIDC users but did not require a user grant before acquiring content or transmitting text to an AI embedding provider. This is unacceptable for an agent handling personal and third-party content.

## Decision

1. Add immutable `ConsentGrant` domain entity: `tenantId` + `ownerSubject` + normalized `source` + `purpose` + `policyVersion` + 90-day maximum lifetime + timestamps of grant/revocation. Only `memory.capture` is supported. No ambient/global allow.
2. Provide Application ports `IConsentRepository`/`IConsentGuard` and a `ConsentService`. An authenticated user must actively acknowledge a versioned policy in a request. The signing OIDC token, not the JSON body, establishes tenant and subject.
3. Store grants in PostgreSQL via forward-only migration `0002`. Concurrency-safe re-grant revokes older scopes in one transaction while preserving history and a unique active-scope index.
4. Validate memory payload **before** any AI call, then check scoped consent. Re-check inside the PostgreSQL memory-write transaction with `SELECT ... FOR SHARE`, so a concurrent revoke and write serialize, avoiding a revoked grant being used for a later commit.
5. Disallow writes with absent/expired/revoked grants and never silently mint implicit grants. Read of already retained data is separate from capture consent.

## Consequences and omissions

Pre-embedding check may race with revocation while an external AI request is already in-flight; this design stops *new* ingestion and later commits, not instantaneous cancellation of requests already issued. A grant does not establish participant consent or a GDPR legal basis. Deletion/export, RLS, encryption, granular OAuth grants, append-only compliance audit, real browser identity, consent UI and legal review remain blocking future features. Tests are xUnit and real PostgreSQL, not a claim of production fitness.
