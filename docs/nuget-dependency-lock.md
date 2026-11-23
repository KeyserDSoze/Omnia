# .NET 10/NuGet deterministic dependency-graph runbook

Project issue [#189](https://github.com/KeyserDSoze/Omnia/issues/189);
parent [#15](https://github.com/KeyserDSoze/Omnia/issues/15);
architecture decision [ADR 0086](../adr/0086-dotnet-nuget-locked-restore.md).

## Invariant

Every `.csproj` under `src/backend` or `tests` has its own
`packages.lock.json`. Do not combine graphs from separate test,
utility and API applications. Root `Directory.Build.props` turns on
lock generation and CI fail-closed restore. The structural verifier
requires the expected framework and NuGet SHA-512 hashes, while
`dotnet restore --locked-mode` catches manifest/transitive drift.

## Clean checkout and CI

```bash
python3 -m unittest discover -s tests/quality -p 'test_nuget_lock.py'
python3 tests/quality/verify_nuget_locks.py
while IFS= read -r -d '' project; do
  dotnet restore "$project" --locked-mode
done < <(find src/backend tests -name '*.csproj' -print0 | sort -z)
dotnet test tests/Omnia.Domain.Tests/Omnia.Domain.Tests.csproj -c Release --no-restore
```

CI performs real PostgreSQL, recovery/PITR, Azure/S3 object-store
contract, Playwright/OIDC and Docker API checks in addition to these
commands. Only GitHub run success for the exact commit is evidence;
this runbook is not itself evidence of a test run.

## Intentionally updating a NuGet dependency

1. Review Microsoft/NuGet advisories and direct/transitive upgrades
   on an isolated, trusted .NET 10 workstation or runner.
2. Modify project PackageReference versions explicitly. Outside
   release/CI jobs, generate new locks for the affected project graphs
   using `dotnet restore --use-lock-file -p:RestoreLockedMode=false
   --force-evaluate`, then review every generated file.
3. Inspect the changes in all referenced graphs, checksum hashes,
   target frameworks and exact versions; re-run offline policy and
   `dotnet restore --locked-mode` for **every** project and all tests.
4. Record ADR/issue when architecture or privileged dependencies change.
   Never automatically enable `--force-evaluate` in CI or bypass
   checksum verification. Never commit NuGet credentials/secrets.

## Non-goals

The .NET SDK patch is still supplied by an unpinned
`actions/setup-dotnet 10.0.x` and Docker `sdk:10.0`; exact runtime
patch/digest lock, SBOM and dependency security scans, signed
attestation, Azure provider readiness and native signed builds remain
open in #15/#10. NuGet package locks do not make package feeds hermetic.
