# CycloneDX 1.6 locked dependency inventory — operational runbook

Related: [ADR 0089](../adr/0089-deterministic-locked-package-cyclonedx-sbom.md),
[#192](https://github.com/KeyserDSoze/Omnia/issues/192), parents #22/#15/#10.

## Local generation and deterministic comparison

Requires Python 3.10+ and repository lockfiles; no npm registry,
NuGet registry, credential, external AI API or user content is read.

```bash
python3 -m unittest discover -s tests/quality -p test_locked_sbom.py
python3 tests/quality/verify_nuget_locks.py
node src/mobile/scripts/verify-mobile-lockfile.mjs
python3 tests/quality/generate_locked_sbom.py --output /tmp/omnia-sbom
python3 tests/quality/generate_locked_sbom.py --verify /tmp/omnia-sbom
sha256sum /tmp/omnia-sbom/*.cdx.json
```

Output: `omnia-npm.cdx.json` and `omnia-nuget.cdx.json`.
Both include versioned package URLs and SHA-512 hashes translated
from npm and NuGet locks, plus source lock ownership, occurrence
counts and unique component counts. NuGet local project references
are excluded because they are not NuGet packages. Repeated identical
package/version/hash occurrences are deduplicated with their owner
paths recorded. No timestamp/random UUID is injected. A changed
lock or output file fails the `--verify` comparison.

## GitHub Actions artifact workflow

Omnia CI runs the unit tests, generates/compares the two BOMs in
`$RUNNER_TEMP/omnia-locked-sbom`, emits their SHA-256 digests, and
uploads `omnia-locked-dependency-sbom` using upload-artifact@v4,
14-day retention, `if-no-files-found: error`. Open the matching
CI run and download its artifact; the CI SHA identifies source input.
Do not upload secrets or customer data; the generator reads only
public package metadata and repository manifests. No repository
contents-write permission is needed.

## Security review and change control

1. Review direct and transitive lock changes via issue/ADR as needed.
2. Regenerate both BOMs; compare PURLs, counts, SHA-512 digests,
   and unexpected new package owners before approving a change.
3. Independently run `npm ci` and `dotnet restore --locked-mode`.
4. Inspect the two archived BOM files and keep CI, OIDC and Pages
   success evidence on the same final commit.
5. **Do not interpret an SBOM as CVE-free**. Run actual SCA/license/
   container scans, provenance/attestation and official CycloneDX
   JSON Schema validation as separate release gates before claiming
   the larger #22 DoD.

The records cover **locked packages**, not actual compiled JavaScript,
NuGet assembly content, runtime/container layers, Android/iOS
bundles, complete dependency edges, publisher signature or code
security. No production/GDPR release claim follows.
