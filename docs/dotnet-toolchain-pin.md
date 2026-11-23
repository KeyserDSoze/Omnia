# Pinned .NET 10 SDK / API Docker patch — operational runbook

Issue [#190](https://github.com/KeyserDSoze/Omnia/issues/190);
[ADR 0087](../adr/0087-dotnet-exact-sdk-and-container-patch.md).

## Baseline

- SDK 10.0.404 (root `global.json`, rollForward=disable,
  allowPrerelease=false)
- Microsoft Docker builder `sdk:10.0.404-noble` (Ubuntu 24.04)
- Microsoft Docker runtime `aspnet:10.0.15-noble`
- NuGet package graphs remain exactly locked in 20 per-project files

The SDK baseline is sourced from the successful CI run 45196289910 and
Microsoft .NET 10 supported container tag tables. Current patch tags
do **not** imply immutable OCI digests or audited security provenance.

## Verify from clean checkout

```bash
python3 -m unittest discover -s tests/quality -p 'test_dotnet_toolchain.py'
python3 tests/quality/verify_dotnet_toolchain.py
test "$(dotnet --version)" = '10.0.404'
python3 tests/quality/verify_nuget_locks.py
dotnet restore src/backend/Omnia.Api/Omnia.Api.csproj --locked-mode
docker build -f docker/Dockerfile.api -t omnia-api-local .
```

GitHub Actions CI runs an actual Docker API build, plus PostgreSQL
and recovery gates. The OIDC workflow validates SDK and drives a real
browser/Keycloak/NET/API/PostgreSQL test. A successful source-policy
test does not prove Docker could pull or build its images.

## Updating for an SDK security patch

1. Review official .NET 10 patch advisories, NuGet packages and
   supported SDK and ASP.NET container tags.
2. Update `global.json`, CI test assertions/policy, Dockerfile
   SDK/runtime FROM tags and actual image assertions together.
3. Re-run the complete clean checkout CI/Pages/OIDC gates and
   separate native smoke when affected. Record new commit and
   evidence in the issue; do not loosen roll-forward to hide failure.
4. Monitor base image rebuilds and check/apply OCI digest pinning,
   SBOM/SCA and signed provenance as **separate** open release gates.

No release, Azure or GDPR compliance is inferred from a pinned SDK.
