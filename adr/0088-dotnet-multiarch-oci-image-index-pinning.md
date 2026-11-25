# ADR 0088 — Microsoft .NET API base images pinned by multiarch OCI index digest

- Date: 2026-11-23
- Status: accepted pending final same-SHA three Actions checks in issue #191
- Parents: #15 (toolchain), #10 (quality); specific change: #191
- Scope: build supply chain for .NET API SDK/runtime container images only

## Context

ADR 0087 version-pinned Microsoft `sdk:10.0.404-noble` and
`aspnet:10.0.15-noble`. However OCI tags can change over time. A
platform-specific manifest digest from a Linux x64 build must NOT be
mistaken for the digest of the multi-platform manifest index, because
that could exclude arm64. Native Android/iOS, PostgreSQL, Keycloak,
NuGet, OIDC, RLS and privacy gates have independent requirements.

## Decision and evidence

- Use an immutable `tag@sha256:<INDEX>` FROM reference for each
  Microsoft container. The exact tag remains visible for human review.
  The hash MUST be of the top-level OCI/Docker manifest **index**
  rather than a single-platform manifest.
- The read-only GitHub Actions
  [#45201861006](https://github.com/KeyserDSoze/Tao/actions/runs/45201861006)
  ran Docker buildx `imagetools inspect --format '{{json .Manifest}}'`
  against the official Microsoft Artifact Registry. Each top-level
  index has SHA-256 image manifest descriptors for **linux/amd64** and
  **linux/arm64**:
  - SDK `mcr.microsoft.com/dotnet/sdk:10.0.404-noble` index
    `sha256:2b1e13a22e46b55117f9cdb4b38e73172de08a30370166c8227d06cdaff82473`.
  - Runtime `mcr.microsoft.com/dotnet/aspnet:10.0.15-noble` index
    `sha256:0bfa6494dead26049c1e0d6c9dc890be79259cb7300f5f23ae1df1cdac3da7df`.
  The discovery workflow had `contents:read` and is removed after
  pinning; the hashes did not come from assumptions or test fixtures.
- TDD RED commit
  [fbf891b](https://github.com/KeyserDSoze/Tao/commit/fbf891b751ec135427b4179fb325ff1b60972d05)
  with [CI #45201860932](https://github.com/KeyserDSoze/Tao/actions/runs/45201860932):
  four negative+positive policy tests PASS; fail-closed on both FROM
  references lacking digest.
- Offline policy tests validate the exact Microsoft registry/repository,
  version tags, digest format and Dockerfile stage count. CI invokes
  `--remote` against the actual Microsoft registry to check index
  mediaType, both platform descriptors and tag-to-expected-index match,
  then performs actual Docker API build with NuGet locked restores.
  This intentionally fails after a registry tag rebuild until an
  upgraded hash is reviewed. OIDC independently runs offline policy.
- Preserve original SDK 10.0.404, runtime 10.0.15 checks, auth/RLS,
  migrations, consent and tenant isolation. Do not loosen safety gates.
- GREEN/REFACTOR commit plus final CI/Pages/OIDC same-SHA evidence
  are tracked in #191; only then close that bounded issue.

## Maintenance and residual risks

Image digest pins are immutable *content references*, not signed
attestations of who built an image or assertions that an image is
up-to-date against newly discovered vulnerabilities. Tag-drift check
does not prove original tag provenance; trust still depends on the
official Microsoft registry and upstream release procedure.
For every security patch, verify release notes, CVEs, index manifest
for amd64/arm64, new digest, clean Docker build, cross-provider
contracts and same-SHA CI/Pages/OIDC. Do not perpetually freeze a
vulnerable digest. Only an amd64 Docker build is actually exercised.
CI itself does not build/run ARM64 and there is no complete SBOM/SCA,
Sigstore, signed OCI provenance, Azure release verification or
other container pinning. Maintain #15 and #10 OPEN.

Official references:
- https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/
- https://docs.docker.com/dhi/explore/security-concepts/digests/
- https://github.com/dotnet/dotnet-docker/blob/main/README.sdk.md
- https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md

## 2026-11-23 amendment — official tag republishing observed (#212)

The same official MCR version tags began resolving to different multiarch
index digests, so the existing strict CI pin contract correctly rejected
both tags: [red CI](https://github.com/KeyserDSoze/Tao/actions/runs/45227656682).
A CI-only conditional **read-only** `--discover` after failure obtained
new index digests on [diagnostic run](https://github.com/KeyserDSoze/Tao/actions/runs/45228302048)
for both linux/amd64 and linux/arm64. Official Microsoft November 10
2026 .NET 10.0.15 security release still names SDK 10.0.404 and runtime
10.0.15 as current: https://dotnet.microsoft.com/en-us/download/dotnet/10.0 .
For the unchanged `10.0.404-noble` and `10.0.15-noble` tags, respectively,
updated only the reviewed index pins to `sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d` and `sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21`.
No claim is made about WHY upstream changed the indexes or whether images
carry current Ubuntu CVE coverage. Existing `--remote` + Docker build +
real SDK/runtime checks remain mandatory. #212 cannot close before
same-SHA CI/OIDC/Pages green. #15/#10 remain OPEN.
