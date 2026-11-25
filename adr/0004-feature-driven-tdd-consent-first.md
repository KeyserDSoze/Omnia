# ADR 0004 — Feature-driven TDD, canonical event + consent-first sequencing

- Date: 2026-11-17
- Status: accepted as working method; future integrations not considered implemented

## Problem

Tao unifies high-risk personal/third-party data and connects AI agents to tools. A single giant implementation would risk uncontrolled access, untestable behavior and false claims of capability.

## Decision

1. Govern work through GitHub epic (#1–#14), feature child issue and labels (`feature`, priority, area, logical phase). Each child maps to a user-observable vertical slice, acceptance and negative tests.
2. Execute TDD with real xUnit, PostgreSQL, provider contract tests, Playwright and native smoke. Review provenance/security before user-data release.
3. Order foundations: opt-in consent ledger and privacy gates → OIDC browser/mobile/authenticated timeline → CanonicalEvent + media/vector memory → approved provider connectors/MCP → grounded insights → separately scoped agency.
4. Keep raw evidence immutable (except deletion), record normalized derivatives and embeddings by model version, allow correction and export. Runtime sensitive data is never injected in source control.
5. Azure via ports/DI plus equivalent OSS docker services, but cloud staging must validate privately; docs and IaC are not live deployments.

## Tradeoffs

Slower short-term feature count but reduced cross-tenant risk and a traceable release process. Initial tenant/consent/secrets/retention are blocked for real data until integration gates pass. Universal OS call interception and WhatsApp personal-chat access are explicitly not product promises.

See `docs/backlog.md`, `docs/product-requirements.md`, `docs/testing-strategy.md`, `docs/platform-capabilities.md`.
