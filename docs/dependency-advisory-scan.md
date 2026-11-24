# Dependency advisory gate — npm + NuGet (failure is actionable)

Issue #193: live SCA gate. Issue #194: initial npm critical/high
findings and mitigation progress. Issue #195: remaining Expo
Router/query-string high vulnerability chain.
Architecture: [ADR 0090](../adr/0090-fail-closed-npm-nuget-advisory-gate.md).

## Reproduce from clean, connected, trusted runner

Requires Node 22/npm, .NET SDK 10.0.404 from global.json, Python
3.10+ and read-only access to official npm and NuGet advisory feeds.

```bash
python3 -m unittest discover -s tests/quality -p test_supply_chain_audit.py
node src/mobile/scripts/verify-mobile-lockfile.mjs
node --test src/mobile/scripts/query-string-compatibility.test.mjs
python3 tests/quality/verify_nuget_locks.py
python3 tests/quality/run_live_advisory_audit.py --output /tmp/omnia-sca/summary.json
```

The generator does NOT call `npm install`, amend manifests or
write lockfiles. It first validates both lock graph inventories,
runs npm advisories for **all** and for **production**; each of the
20 NuGet projects runs a locked-mode, transitive audit via official
feeds. Output `status` is `pass`, `blocked`, or `incomplete`.
A scanner outage, missing JSON or mismatched project is a failure,
not a successful clean scan. `blocked` means audited HIGH/CRITICAL
remain unaccepted; `incomplete` means the scan itself failed.
The GitHub Actions `security-audit` job is **required and fail-closed**
within CI, and uploads `omnia-security-audit` (JSON, 14-day
retention) even on failure. Do NOT interpret artifact upload as PASS.

## Current blocker — as observed 2026-11-23

| Scope | Critical | High | Moderate |
|---|---:|---:|---:|
| npm locked packages before remediation | 2 | 3 | 4 |
| npm full graph after validated Vitest 5.1.1 upgrade | 0 | 3 | 3 |
| npm production graph after upgrade | 0 | 3 | 3 |
| NuGet 20 audited projects at observed CI time | 0 reported | 0 reported | 0 reported |

No unscoped or active waiver was used. The three HIGH npm package
identities are `decode-uri-component@0.2.2`,
`query-string@7.1.3`, `expo-router@57.0.32`. The directly linked
published HIGH advisory is
https://github.com/advisories/GHSA-qr28-fpjm-wq7f for
decode-uri-component; the other two are npm meta-vulnerabilities
in the chain.

Vitest 5.1.1 was verified on a disposable CI runner with `npm ci`,
TypeScript, 61 Vitest, Expo Web export, static Playwright, and both
npm audit scopes. It removes tinypool CRITICAL advisories
https://github.com/advisories/GHSA-5gmw-xhrv-c9v3 and
https://github.com/advisories/GHSA-85c8-ppgw-ccpr.
A direct override to patched decode-uri-component 0.5.1 is
**NOT COMPATIBLE** with query-string 7 CommonJS; it failed a runtime
regression with `TypeError: decodeComponent is not a function`.
Do not reintroduce the override. The required next step is a
coordinated official Expo SDK/Router migration in #195, including
OIDC, Web and native smoke. Do not claim release readiness
while the security gate is blocked.

## Exceptions (rare; do not use to hide these HIGH findings)

`security/audit-exceptions.json` is tracked, version 1, with
`exceptions: []` initially. Future accepted exceptions require:
ecosystem npm/nuget, exact package and locked version, HTTPS advisory
ID, explicit owner, Omnia GitHub issue link, concrete mitigation
reason, and UTC YYYY-MM-DD expiry within 1–30 days from the scan.
Expired, irrelevant, unscoped, duplicate or stale exemptions fail
the gate. npm indirect meta-vulnerabilities cannot be waived by
fabricating a synthetic identifier. Document real exploitability
and compensating controls on the issue, and remove an exception
promptly when remediated.

## Coverage limitations

NuGet's zero reported advisories reflect only packages and advisory
feed information observed at the scan time, not a perpetual
guarantee. The job does not scan OCI layers, OS/Keycloak/PostgreSQL
images, signed provenance, malicious dependencies, binary runtime
bundles, licenses, source SAST or Android/iOS artifacts. It does
not determine whether a CVE can be exploited in a specific code path.
Release gates for privacy, consent, RLS, Azure and GDPR remain open.

## Mandatory moderate review and expiry (#215 / ADR 0094)

See [moderate advisory review runbook](moderate-advisory-review.md). After the live high/critical SCA gate succeeds, CI independently checks that all current npm MODERATE warnings are covered by a precise, owner-tracked, expiring risk review. This does not waive, hide or patch findings; unexpected or expired entries make CI fail. The initial 2026-11-24 review is due 2026-12-08 UTC. The actual npm SCA artifact, not old tables in this document, is authoritative.
