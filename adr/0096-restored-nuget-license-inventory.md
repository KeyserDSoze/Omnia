# ADR 0096 — Inventory license declarations from restored NuGet archives

- Date: 2026-11-24
- Status: accepted for bounded implementation; external CI validation pending
- Child #217; parents #22/#10 and related #131 remain open.

## Decision

Reuse ADR 0086 locked restores and ADR 0089 lock-derived package
identity/hash inventory, in a dedicated ephemeral global-packages cache.
Read only verified actual .nupkg archives, not standalone extracted
metadata. For signed packages delegate content hash calculation and
signature verification to the pinned .NET 10 NuGet command; for unsigned
archives calculate raw SHA-512. Compare to the canonical locked content
hash. Record raw archive SHA-256 separately. Require exact identity,
normalized version and one valid root manifest; reject unsafe/ambiguous
archive paths, malformed XML, missing license files and hash mismatch.
Do not extract or execute archive content or fetch legacy license URLs.

Preserve license expressions and file declarations; for a referenced file
record SHA-256. Mark legacy URL and missing license metadata explicitly
as additional-review. Complete inventory does not approve terms or
eliminate release obligations. No SPDX OR resolution, no exception or
suppression of vulnerability gates, no dependency graph changes.
Publish a deterministic report tied to the same commit, from the
security-audit job after successful audited locked restores.

## Evidence

Local behavioral RED with incomplete stub: nine test methods, 10
failures and 5 errors. GREEN: nine methods passed using synthetic ZIP
archives and mocked signed-verifier output. The local environment has
no .NET SDK: real signed archives and restore verification must be
confirmed by GitHub Actions before #217 closure. See
docs/nuget-license-inventory.md and tracked issue #217.

Legal review of distribution notices, source obligations, actual
runtime/container artifacts, repository licensing and owner approval
remains open. A successful NuGet verification is not a complete
publisher-trust policy or production-release approval.
