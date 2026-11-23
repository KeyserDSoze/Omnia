# ADR 0089 — Deterministic lock-derived CycloneDX package inventories

- Date: 2026-11-23
- Status: accepted after confirmation of same-SHA Actions in #192
- Bounded issue: #192; parents #22, #15, #10 stay open
- Scope: CI-generated package manifest inventories, not actual runtime SBOM or SCA

## Context and constraints

Omnia pins Expo/npm v3 dependency resolution (ADR 0085) and all 20
.NET/NuGet package graphs (ADR 0086). There was no standardized
machine-readable inventory of the exact package versions and hashes
for review or artifact retention. Manually written package lists
are incomplete and can drift independently. Since Omnia is a
privacy-sensitive SaaS under development, dependency metadata
only—not user data, environment secrets or signed credentials—may
be included in these artifacts.

## Decision

- Generate separate CycloneDX JSON **1.6** inventories for the npm
  `src/mobile/package-lock.json` dependency entries and every
  `packages.lock.json` matching backend/test .csproj. Include direct
  and transitive references; skip NuGet ProjectReference records,
  which are not NuGet packages.
- Map each resolved component to a PURL and SHA-512 package content
  checksum in lowercase hex, preserving the source lock path and
  target framework/owner via CycloneDX properties. Deduplicate the
  same package/version with the same hash, **reject** collisions
  with conflicting hashes. Publish both package occurrence counts
  and unique component counts. An npm package nested under a different
  node_modules hierarchy is still counted as a locked occurrence.
- Fail closed when a lock is missing or has unexpected structure,
  npm direct dependency missing, project target framework absent,
  declared direct NuGet PackageReference not locked, package version
  missing, unsupported package type, foreign npm registry URL or
  invalid SHA-512 hash. These are source/lock gates, in addition to
  npm ci and dotnet restore --locked-mode, not replacements.
- Make output byte-identical for the same input: UTF-8 JSON sorted keys,
  components sorted by PURL, lock owner properties sorted, no ephemeral
  timestamps or UUIDs. CI generates two files in RUNNER_TEMP, runs
  --verify to compare them exactly, prints SHA-256 file digests for
  audit and uploads them under one artifact with a 14-day expiry,
  `if-no-files-found: error`, minimal repository read permissions.
- Use Python standard library and unchanged .NET/Expo locks, with
  no new registry queries during SBOM generation.
- Do **not** write massive derived package lists directly into
  `main`; GitHub Actions artifacts are tied to the source commit.

## TDD evidence

- RED commit
  [2027d7f](https://github.com/KeyserDSoze/Omnia/commit/2027d7fcca87bfb359157279056ae7aab8817f79)
  and [CI 45205686015](https://github.com/KeyserDSoze/Omnia/actions/runs/45205686015):
  test-first module imported an as-yet-unimplemented generator, failing
  with ModuleNotFoundError as expected; this was **not** a behavioral
  assertion failure, and we do not claim one.
- GREEN commit
  [5cfc081](https://github.com/KeyserDSoze/Omnia/commit/5cfc081010867ad6d948cdc27495d1415e874f31):
  generator implemented, 5 real positive/negative tests and
  generation/verification pass in CI #45206294336; artifact
  `omnia-locked-dependency-sbom` id 14833274152 uploaded.
- REFACTOR and 3 final same-SHA CI/Pages/OIDC links recorded in
  [#192](https://github.com/KeyserDSoze/Omnia/issues/192) after
  real completion, not inferred from initial upload alone.

## Deliberate limitations and next steps

This is a **package-lock inventory**, not an inventory of deployed
runtime layers, static/bundled JS/assemblies or binary/image contents.
NuGet may refer to packages on multiple targets; no complete
runtime dependency-edge graph is claimed. The JSON is constrained by
producer unit tests and expected CycloneDX fields, but **no separate
official CycloneDX JSON Schema validation step** is claimed in this
slice. The source lock digest proves data consistency but not
the publisher's identity or absence of known vulnerabilities.
License identification, malware/vulnerability scans, policy thresholds,
CodeQL, Dependabot, secret scanning, image attestation and SLSA
provenance are separate unmet #22/#15/#10 requirements.
No production deployment or GDPR compliance is implied.

References:
- https://github.com/CycloneDX/specification/blob/master/schema/bom-1.6.schema.json
- https://ecma-tc54.github.io/ECMA-424/
- https://docs.github.com/en/actions/tutorials/store-and-share-data
