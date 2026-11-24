# NuGet license inventory from restored packages (#217)

This complements the npm declaration inventory (#216) and the
lock-derived CycloneDX SBOM (#192). It is **evidence of package identity,
content integrity and license metadata**, not legal clearance or a scan
of the published runtime.

## Restore and inventory

The security-audit job uses a dedicated runner-temporary NUGET_PACKAGES
directory for the existing audited locked restores of all 20 projects.
The inventory runs only after the existing gates pass. No new package,
SDK dependency, lock update, credentials or extra registry download
is introduced by the inventory itself.

The existing NuGet SBOM generator supplies all deduplicated package
identities and content hashes, rejecting conflicting versions/hashes
and preserving owning projects/TFMs. For each identity, inspect the
actual .nupkg in the restore directory, not a loose extracted .nuspec
or a cached .sha512/.nupkg.metadata file.

For signed packages, use .NET 10's official dotnet nuget verify command
and require a successful exit and exactly one canonical Content hash
matching the lock. NuGet's signed content hash excludes the signature;
a raw ZIP SHA-512 is not equivalent. Unsigned packages use a freshly
computed SHA-512. Each row also includes raw archive SHA-256, so a
changed signature/archive is visible even when content remains identical.
Verification failures or timeouts block the job; no raw-hash fallback
for signed archives. Signature command success is not a separate
publisher allowlist or full supply-chain attestation.

Read the single root .nuspec and check the package/metadata namespace,
ID (case insensitive) and normalized version. XML DTD/entities, unsafe
or ambiguous ZIP paths, duplicate metadata, missing archives and missing
declared license files fail closed. No archive is extracted or executed.
Read bounded metadata and license-file bytes in memory, never download
a licenseUrl. Record a declared file's archive path and SHA-256.

## Report interpretation

- expression: preserve the declaration exactly; common exact expressions
  get notice-review, others additional-review. Never choose an OR branch.
- file: record path and SHA-256; additional-review.
- legacy-url: record the legacy declaration; additional-review, not fetched.
- missing: explicitly null; additional-review, not a license grant.

An inventory-complete report means all locked identities were inspected
and all hashes/structure matched. Additional-review, including missing
metadata, is intentionally visible and still requires human assessment
before release. It is not automatically classified as incompatible or
approved. This scope does not verify the complete SPDX grammar, publisher
trust policy, runtime dependency graph or obligations in binaries.

Reproduce from the repository root with the pinned .NET SDK and the
existing live audit (requires network and configured trusted feeds):

```sh
export NUGET_PACKAGES="$(mktemp -d)"
python3 tests/quality/run_live_advisory_audit.py --output /tmp/omnia-sca.json
python3 -m unittest discover -s tests/quality -p test_nuget_license_inventory.py
python3 tests/quality/nuget_license_inventory.py --cache "$NUGET_PACKAGES" --output /tmp/omnia-nuget-licenses.json
```

CI uploads omnia-nuget-license-inventory for 14 days; the report is
deterministic for the same source SHA and archive bytes and contains
no package contents, credentials or user data. Review actual notice and
license text obligations for distributed software, image layers and
Omnia's own licensing decision under #22/#131.

References:
- https://learn.microsoft.com/en-us/nuget/reference/nuspec
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-verify
- https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders
- https://github.com/NuGet/Home/wiki/Nupkg-Metadata-File
