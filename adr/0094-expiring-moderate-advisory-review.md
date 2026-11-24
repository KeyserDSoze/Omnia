# ADR 0094 — Expiring, exact review gate for npm moderate advisories

- Date: 2026-11-24
- Status: accepted as bounded source policy; external Actions confirmation required
- Child #215; parent #196, #22, #10 and #15 remain OPEN

## Problem and evidence

Omnia's live SCA (ADR 0090) rejects unreviewed HIGH/CRITICAL, but six npm MODERATE warning records can remain visible indefinitely without review. The CI artifact for source SHA \`b442bdc2\` identifies three distinct package identities in both full and production npm scopes: \`minimatch@10.2.6\` (GHSA-g3hv-75wg-m249), \`@expo/fingerprint@0.21.10\` and \`expo@58.0.26\` (npm indirect meta-vulnerabilities). These are the Expo Node tooling chain, not three unrelated CVEs. Public advisory fix minimatch 11.0.1 is **not automatically compatible** with Expo fingerprint's minimatch 10 dependency; Expo maintainers describe a build-tool-only reachability boundary, subject to verification in our product.

## Decision

- Add exact, scoped, **non-waiving** review metadata under \`security/moderate-advisory-reviews.json\` with responsible GitHub account, issue, GHSA root, explanation, compensating controls and no more than 14 days before review expiry.
- Add a Python standard-library gate that reads the canonical SCA \`summary.json\`, verifies \`status=pass\`, same GitHub commit SHA, two complete npm audit scopes and 20 NuGet reports, then compares every npm MODERATE warning to an exact reviewed identity and scope. Reject new/stale/missing warning identities, unsupported wildcard, duplicate review, invalid metadata, future or expired review and generic waiver fields. Never change npm locks, replace scanners or reduce the severity of a warning.
- Wire the check **after** the unchanged live SCA step and before the existing \`if:always()\` artifact upload; any mismatch makes security-audit fail. HIGH/CRITICAL and incomplete scanner source paths still fail at the original gate.
- Keep the review time-bounded to UTC **2026-12-08**. A forced upgrade to minimatch 11 is not acceptable without upstream-supported Expo peers and actual Web/OIDC/native device tests; the current risk remains visible, not approved for production.

## TDD evidence and limits

RED: local Python test module failed with \`ModuleNotFoundError\` before the verifier existed (bootstrap RED, not a behavioral assertion). GREEN: eight independent negative/positive unit tests PASSED locally; the checker also PASSED against the **downloaded official GitHub SCA artifact** for the historical SHA b442bdc2. These do not establish full CI/OIDC/Pages/CodeQL on the new commit; close #215 only after real same-SHA workflows succeed.

No broad security exception or permanent moderate waiver is introduced. This is review accountability, **not remediation**. Requires a fresh human review by its expiration even if no package changes. Limitations: actual exploit reachability, container image CVEs, non-npm advisories, supply-chain provenance, signed native builds, and GDPR release gates remain separate.

References:
- https://github.com/advisories/GHSA-g3hv-75wg-m249
- https://github.com/expo/expo/issues/52735
- https://docs.expo.dev/versions/v58.0.0/sdk/fingerprint/
