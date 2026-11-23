# Dependabot — controlled dependency update proposals (#197)

GitHub Dependabot **version updates** are configured in `.github/dependabot.yml` on `main`. The file uses the JSON-compatible subset of YAML 1.2 so that the *exact* configuration can be parsed and policy-checked using only Python's standard library (no new unpinned YAML-parser dependency). It is also parseable by YAML tools.

| Ecosystem | Manifest locations | Cadence / cap |
|---|---|---|
| npm | `/src/mobile` | Weekly / 5 open version PRs |
| NuGet | `/src/backend/*`, `/tests/*` | Weekly / 5 open version PRs; group the same dependency across projects |
| GitHub Actions | `/` (`.github/workflows`) | Weekly / 5 open version PRs |
| Docker | `/docker` (`Dockerfile.api`) | Weekly / 5 open version PRs |

Dependabot creates **reviewable pull requests** against the default `main` branch. Configuration **does not enable automerge**; the owner reviews any proposed update and its lockfile, security and compatibility impact. Do not silently bypass CI, switch target branches, force npm peer resolution or merge a proposed upgrade before review. The update caps are per ecosystem for version PRs, not global caps on security updates.

The CI preflight runs `python3 -m unittest discover -s tests/quality -p 'test_dependabot_policy.py'` and `python3 tests/quality/verify_dependabot_policy.py`. It rejects missing ecosystems, disabled/excessive PR limits, nonweekly cadence, unreviewed grouping, target branches, ignored update ranges, and new NuGet projects outside the supported wildcard directories. The policy verifier is **not** a substitute for GitHub executing Dependabot, validating access to registries, or sending its first PR. After main changes, inspect Repository → Insights → Dependency graph → Dependabot and the resulting PR/check statuses; Dependabot alert/security-update enablement can require separately configured repository settings.

**Mandatory per-PR checks** are unchanged: SDK 58/React Native ABI and `npm ci` lock checks, `dotnet restore --locked-mode` for all projects, runtime/SDK pins, Docker multiarch OCI index digest verification, advisory/SCA audit, full CI, real OIDC/Keycloak/Postgres and Pages. A Docker tag or digest change will intentionally fail the pin policy until the reviewed image, registry manifest index, ADR and docs are updated together; a bot cannot approve a production image. EAS-signed/device native smoke, CVE/licenses and attestation remain separate outstanding #195/#22/#15/#10 gates.

**Scope / limits:** No deployment or third-party data access, no secrets, no auto-merge, no assertion that a PR has been created or that GitHub security updates are enabled. This slice completes only the version-update *configuration and local regression gate*. No production/GDPR readiness claim.

References: [GitHub Dependabot options](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference), [Dependabot config file](https://docs.github.com/en/code-security/concepts/supply-chain-security/about-the-dependabot-yml-file), [ADR 0092](../adr/0092-controlled-dependabot-version-updates.md).
