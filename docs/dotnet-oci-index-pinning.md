# .NET API Docker OCI index pins — security update runbook

Linked: [ADR 0088](../adr/0088-dotnet-multiarch-oci-image-index-pinning.md),
[issue #191](https://github.com/KeyserDSoze/Omnia/issues/191),
parents #15 and #10.

The Docker API uses Microsoft MCR `sdk:10.0.404-noble` and
`aspnet:10.0.15-noble`, each accompanied by an OCI/Docker **index**
digest. Both indexes were queried independently via Docker buildx
on a disposable read-only GitHub runner and had linux/amd64 and
linux/arm64 descriptors. The Docker API build in CI is amd64 only.

## Read-only local or runner verification

```sh
python3 -m unittest discover -s tests/quality -p test_dotnet_image_pins.py
python3 tests/quality/verify_dotnet_image_pins.py --offline
python3 tests/quality/verify_dotnet_image_pins.py --remote
python3 tests/quality/verify_dotnet_toolchain.py
docker build -f docker/Dockerfile.api -t omnia-api:test .
```

The `--remote` check requires `docker buildx imagetools` and registry
access. It verifies the digest already pinned in Dockerfile and compares
the currently published version tag to that digest (fail-closed on
drift). The build must successfully fetch the pinned digest and
assert SDK/runtime versions, then use NuGet locked restores.

## Security refresh (intentional, not silent)

1. Evaluate Microsoft .NET security advisories and Ubuntu Noble base
   image CVEs; review the new SDK and ASP.NET release tag versions.
2. Discover the current top-level manifest INDEX for each proposed
   official Microsoft tag:
   `docker buildx imagetools inspect --format '{{json .Manifest}}'
    mcr.microsoft.com/dotnet/sdk:VERSION-noble`.
   Confirm index mediaType and **both** linux/amd64 and linux/arm64
   child manifest descriptors, not just the runner's amd64 digest.
3. Update together the Dockerfile `tag@sha256:index` pair,
   `global.json`, toolchain and image policy constants/test data,
   documentation and relevant NuGet graph if it changed. Inspect
   the diff, do NOT fall back to floating tags after registry failure.
4. Re-run offline/remote tests, actual Docker API build, SDK/runtime
   assertions and all same-SHA CI, Pages and true OIDC E2E jobs.
   Require a real ARM64 runner/build before claiming ARM64 execution.
5. Record the security reason, digest values, tested architecture,
   official release sources and CI run URLs on a GitHub issue.
   Review upstream signature/attestation and SBOM/SCA separately.

Tags and indexes may be republished/garbage-collected. An immutable
digest is not a malware scan, signature verification, vulnerability
patch policy, multiarch execution test or production readiness claim.

## Automatic read-only diagnostic on official tag drift (#212)

When the independent remote pin check fails (for example, if an
official Microsoft tag points to a different index), CI keeps the
**blocking failure** and immediately invokes
`python3 tests/quality/verify_dotnet_image_pins.py --discover` under
`if: failure()`. The output identifies the currently published official
top-level indexes and supported linux/amd64 and linux/arm64 platforms
for manual review. It does not edit the Dockerfile, upload secrets,
automatically trust an index, or turn CI green. Dedicated policy tests
reject removing `--remote`, using `always()`, moving discovery before
the blocking gate, or adding `continue-on-error`. A real registry
query followed by a reviewed version/digest patch and actual Docker
build is still required; see [#212](https://github.com/KeyserDSoze/Omnia/issues/212).

## 2026-11-23 reviewed MCR tag index drift (#212)

GitHub CI [45227656682](https://github.com/KeyserDSoze/Omnia/actions/runs/45227656682)
detected **both** tag→index mismatches and correctly failed before
NuGet restore. Read-only discovery via [45228302048](https://github.com/KeyserDSoze/Omnia/actions/runs/45228302048)
resolved the official registry index for each **unchanged** version tag
and verified `linux/amd64` plus `linux/arm64` descriptors:

| Official MCR tag | Previously reviewed index | New observed index |
|---|---|---|
| `dotnet/sdk:10.0.404-noble` | `sha256:2b1e13a22e46b55117f9cdb4b38e73172de08a30370166c8227d06cdaff82473` | `sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d` |
| `dotnet/aspnet:10.0.15-noble` | `sha256:0bfa6494dead26049c1e0d6c9dc890be79259cb7300f5f23ae1df1cdac3da7df` | `sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21` |

Microsoft's official [November 2026 .NET 10 security release](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
lists SDK **10.0.404** and runtime **10.0.15** as the current patched
pair. The [November 10 release notes](https://github.com/dotnet/core/blob/main/release-notes/10.0/10.0.15/10.0.15.md)
cover security fixes. Both tag indexes were independently read from
official MCR in disposable runner; *why* the tags were republished is
not asserted. New pins were committed together for a full **remote
index and real Docker build** verification. Until the full same-SHA
CI/OIDC/Pages are successful, #212 stays OPEN. This is not evidence
of registry signature verification, image CVE scanning, attestation,
real arm64 execution, or production-readiness.
