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
