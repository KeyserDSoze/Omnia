# ADR 0090 — Fail-closed npm/NuGet advisory audit, no silent suppressions

- Date: 2026-11-23
- Status: accepted (security gate implemented, **currently blocking** due to HIGH findings)
- Scope: live dependency SCA for pinned npm v3 + twenty .NET 10 NuGet graphs
- Issues: #193 audit gate; #194 critical/high npm remediation; #195 Expo SDK/Router upgrade
- Parents remain OPEN: #22 supply chain, #15 toolchain, #10 quality

## Threat, context and decision

A lockfile and a CycloneDX inventory (ADR 0089) establish **what**
packages were selected, NOT whether any of those packages have
currently known vulnerabilities. Treat missing advisory responses
and ambiguously parsed output as a **failed gate**, never as proof
of zero CVEs. Separate scanner failures (`incomplete`) from valid
reports that contain HIGH/CRITICAL packages (`blocked`).

1. Read-only CI `security-audit` job (distinct from mobile/dotnet)
   validates checked-in npm v3 and all 20 per-project NuGet locks,
   generates existing two lock-derived SBOMs and queries actual
   **npm registry** audit for both complete and production-only
   (`--omit=dev`) scopes. npm scanner exit codes 0/1 can be valid
   audit results; reject other statuses, feed errors, broken schema,
   inconsistent metadata counts and missing direct/transitive
   lock package identity.
2. For **each** .NET project, perform an audited
   `dotnet restore --locked-mode -p:NuGetAudit=true
   -p:NuGetAuditMode=all -p:NuGetAuditLevel=low` and then
   `dotnet package list --project ... --vulnerable
   --include-transitive --format json --output-version 1
   --no-restore`. Enforce the official NuGet version-1 JSON
   project/parameters/advisory sources contract; valid clean
   projects can omit frameworks. Guard against advisory feed error
   NU1900/NU1905 and unknown packages not in their lock.
3. Block **all** unmitigated HIGH and CRITICAL. Report MODERATE/LOW;
   do not silently discard them. npm meta-vulnerability chains without
   direct URL are not waivable via a synthetic advisory. Output
   machine-readable JSON with `pass` / `blocked` / `incomplete`
   and the Git SHA. A `security-audit` job failure remains failure.
   Upload the 14-day report via `if:always()` for transparency only.
4. No waiver by default: `security/audit-exceptions.json` starts
   empty. Any future exception must match an exact ecosystem,
   package, resolved version and advisory URL plus mitigation reason
   at least 30 chars, owner, tracked Omnia GitHub issue, and expiry
   strictly within 30 days. Expired, stale, duplicate or wildcard
   exceptions **fail**, rather than giving a clean result.
5. Preserve the lockfile restoration and image-digest gates;
   never run auto `npm audit fix --force`, disable auditing,
   pretend a scanner outage is clean, automatically update locks
   from a security report or weaken tests to greenwash CI.

## Test-first evidence and observed real findings

- RED: [commit b72719b](https://github.com/KeyserDSoze/Omnia/commit/b72719b784597e15922797906819b9a18c5f5dd3)
  / [CI 45210743119](https://github.com/KeyserDSoze/Omnia/actions/runs/45210743119):
  six behavioral tests fail on provisional unsafe parser for
  critical/high rejection, malformed/failed reports, exact expiring
  waivers and suspicious indirect findings. Failure was expected.
- GREEN policy: [commit 63685a2](https://github.com/KeyserDSoze/Omnia/commit/63685a2a37a78ff021518cfa5fde83688304442f);
  [CI 45213586840](https://github.com/KeyserDSoze/Omnia/actions/runs/45213586840)
  has **8/8** policy unit tests green, then real NuGet scans
  **20/20 projects, 0 advisories reported**, and expected red
  overall job due to npm vulnerabilities. Validated NuGet CLI
  --project syntax and clean no-framework JSON from official output.
- Real npm discovery:
  [45212260879](https://github.com/KeyserDSoze/Omnia/actions/runs/45212260879)
  returned 2 CRITICAL / 3 HIGH / 4 MODERATE in locked npm graph.
  CRITICAL via tinypool@1.1.1 and Vitest@3.2.7, HIGH via
  decode-uri-component@0.2.2, query-string@7.1.3 and
  expo-router@57.0.32. HIGH occurs even in the production scope.
- REMEDIATION: [isolated trial 45215428007](https://github.com/KeyserDSoze/Omnia/actions/runs/45215428007)
  SUCCESS: only Vitest test runner to 5.1.1 with a fresh locked
  npm graph, npm ci, TS, 61 Vitest, Expo export, Playwright static,
  and official npm audit **0 critical / 3 high / 3 moderate**.
  Commit to main:
  [d6b9af2](https://github.com/KeyserDSoze/Omnia/commit/d6b9af2900a5ca3c1c5da1be9ed519bbc136b6c7).
  npm package manifest/lock changed atomically after real tests.
- REJECTED: a transitive `decode-uri-component@0.5.1` override
  was found incompatible with `query-string 7` CommonJS:
  [trial 45214808484](https://github.com/KeyserDSoze/Omnia/actions/runs/45214808484)
  failed `TypeError: decodeComponent is not a function`
  in an explicit new regression test; NO incompatible override was
  committed. #195 tracks a coordinated supported Expo SDK/Router
  upgrade rather than an untested forced major.

## Residual blockers and explicit non-goals

The SCA gate must **remain RED** while three HIGH npm production
vulnerabilities exist. Closing #193 or #194 early would misrepresent
the repository security status. Do not claim full CI SUCCESS at
a security-blocked SHA: the mobile, .NET, Pages and OIDC suites may
pass individually while SCA correctly fails.
No comprehensive container-image/OSS CVE scan, package publisher
attestation, malware/secret scan, licensing audit, static source
analysis, native device smoke, Azure release or GDPR readiness is
provided by this tranche. A live zero NuGet finding is a temporal
statement about the queried registry, not proof of eternal safety.

Official references:
- https://docs.npmjs.com/cli/commands/npm-audit
- https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages
- https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-list
