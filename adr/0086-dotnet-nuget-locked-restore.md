# ADR 0086 — NuGet project-local locked restore across all .NET 10 graphs

- Date: 2026-11-23
- Status: accepted for .NET source/package restoration; full SDK/release pinning pending
- Parent quality issues: #15 and #10; bounded work issue: #189
- Scope: source dependency supply chain only; no changes to JWT/OIDC, tenant RLS,
  encrypted memory, consent, recovery authority or personal data handling

## Context and threat

Before this change 20 different .NET projects implicitly executed
unlocked NuGet restores: API, migration/erasure/audit utilities and
PostgreSQL/test tools. An identical source tree could resolve differently
when a transitive package changes. No official NuGet package lock had
been committed. This makes CI a moving target, not a security claim.

## Decision

1. Set `RestorePackagesWithLockFile=true` in root `Directory.Build.props`
   for every project; set `RestoreLockedMode=true` in CI, including
   implicit `dotnet run/build/test` restores.
2. Generate **all 20** independent `packages.lock.json` files using
   the .NET 10 SDK on an ephemeral Ubuntu GitHub runner, from the
   official https://api.nuget.org/v3/index.json source. The one-time
   bootstrap explicitly overrides the CI lock policy solely during
   initial generation, then independently restores **all 20** projects
   using `dotnet restore --locked-mode`. Build the API and run xUnit
   with no implicit restore prior to committing.
3. Validate NuGet JSON graphs offline before any restore: projects,
   net10.0 targets, every direct PackageReference present in a locked
   direct entry, resolved versions and base64 SHA-512 `contentHash`
   on NuGet packages. ProjectReference entries do not have NuGet hashes.
   The structural validator does not replace the authoritative NuGet
   locked restore.
4. CI explicitly restores all 20 project roots with `--locked-mode`.
   The real-browser OIDC workflow checks the same inventory and locks
   the API graph. Docker API build copies the root props and each
   project directory/lock graph, performs `dotnet restore --locked-mode`,
   then `dotnet publish --no-restore`. No CI fallback to
   `--force-evaluate` or silent graph update is permitted.
5. The temporarily privileged bootstrap workflow is deleted after
   publishing the reviewed generated locks as a single non-force,
   fast-forward Git commit. The normal release workflows remain
   read-only to GitHub repository contents.

## Test-driven evidence

- RED: [commit a7bd1e7](https://github.com/KeyserDSoze/Omnia/commit/a7bd1e7d39312cc22dbace67134648942618acbf)
  and [CI run 45195314141](https://github.com/KeyserDSoze/Omnia/actions/runs/45195314141):
  all four policy unit tests PASS; offline preflight FAIL explicitly on
  the **20** missing NuGet lockfiles before any .NET restore.
- GREEN: [bootstrap run 45195600872](https://github.com/KeyserDSoze/Omnia/actions/runs/45195600872)
  SUCCESS: generated 20 graphs, independently restored all 20
  with `--locked-mode`, built the API and tested the xUnit domain suite
  without restore; published [lock commit 736794e](https://github.com/KeyserDSoze/Omnia/commit/736794e3468303f84b014a0124f3b08c9850be02).
- REFACTOR: added explicit CI/OIDC/Docker locked restore, updated docs
  and removed the bootstrap workflow. Final CI, OIDC, Pages workflow
  statuses must be reported against the SAME refactor commit in #189
  before closing.

## Non-goals, residual risks

The lockfile pins NuGet package versions and hashes, NOT the binary
reproducibility of a compiled output, exact .NET SDK and runtime patch
image, host OS, Microsoft workload manifests, feed availability or
security of a package already pinned. Add SBOM, SCA and attestation, exact
SDK/container digest pinning and native device smoke under #15/#10.
Production, Azure and real data compliance are not implied.

Official references:
- https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-restore
- https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1004
