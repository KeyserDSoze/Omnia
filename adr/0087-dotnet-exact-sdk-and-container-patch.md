# ADR 0087 — Exact .NET SDK and container patch baseline

- Date: 2026-11-23
- Status: accepted (subject to same-SHA Actions CI/OIDC/Pages evidence in #190)
- Related issues: #190 (bounded); #15 and #10 remain open
- Scope: deterministic .NET tooling, not release attestation or personal data

## Context

Omnia already locks all 20 NuGet graphs (ADR 0086) yet CI/OIDC used
`dotnet-version: '10.0.x'` and the API Dockerfile pulled unbounded
`sdk:10.0` and `aspnet:10.0` tags. Re-running the same source in a
future month could select a new SDK toolchain and runtime patch.
GitHub CI run 45196289910 (dotnet setup logs) actually selected
**SDK 10.0.404** and Core Runtime **10.0.15** on 2026-11-23.
The official Microsoft .NET 10 SDK Docker supported tag table lists
`10.0.404-noble`; official ASP.NET Docker supported tags list
`10.0.15-noble`. These use Ubuntu 24.04 (Noble).

## Decision

1. Root `global.json`: SDK `10.0.404`,
   `rollForward: disable`, `allowPrerelease: false`.
   Future SDK security upgrades require deliberate reviewed changes.
2. CI and real-browser OIDC GitHub Actions use
   `actions/setup-dotnet@v4` with `global-json-file: global.json`,
   not `dotnet-version: 10.0.x`. Both execute and assert
   `dotnet --version == 10.0.404` before NuGet locked restores.
3. API build image uses official `sdk:10.0.404-noble`, copies
   `global.json` and fails the actual Docker build if SDK differs.
   Runtime image uses `aspnet:10.0.15-noble` and checks its actual
   .NETCore.App and ASP.NETCore.App framework versions.
4. Python policy validator checks the source pin and workflow/Docker
   invariants with four negative/positive unit tests. Actual GitHub
   Actions and Docker build verify behavior independently. Do not
   bypass missing SDK with roll-forward or a floating image tag.
5. No API contracts, JWT/RLS, consent, export/erasure, cryptographic
   keys, migrations, E2E scenarios or security gates are altered.

## Evidence (test-first)

- RED `4ff7ab0`: four pure policy tests PASS; live CI fails
  *for exact missing SDK pin/global.json and floating workflows/images*.
  https://github.com/KeyserDSoze/Omnia/actions/runs/45199024939
- GREEN and REFACTOR: commit/action IDs tracked in #190 after all
  three same-SHA workflows complete; no status inferred from a
  successful policy alone.

## Limitations and operations

A fixed version **tag** is NOT an immutable OCI digest, even though
it prevents automatic SDK/runtime version selection when correctly
checked. Microsoft may rebuild the same patch tag to incorporate OS
security updates; OCI image digest pinning, provenance/attestation and
SBOM/scanning are future steps under #15/#10. .NET SDK patch is frozen
but Node, Playwright/browser, runner host, npm registry, native toolchain
and Azure staging are not collectively bit-for-bit reproducible.
Security servicing requires a tracked, intentional, audited upgrade of
global.json, Docker tags and tests, retaining NuGet locked restores.

Official references:
- https://learn.microsoft.com/en-us/dotnet/core/tools/global-json
- https://github.com/actions/setup-dotnet
- https://github.com/dotnet/dotnet-docker/blob/main/README.sdk.md
- https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md
- https://github.com/dotnet/dotnet-docker/blob/main/documentation/supported-tags.md
