# Moderate npm advisory review — fail-closed human accountability (#215)

## Evidence and limited risk assessment (2026-11-24)

[Official live SCA artifact](https://github.com/KeyserDSoze/Omnia/actions/runs/45228566700) on commit b442bdc2 scans the complete npm lock and `--omit=dev` production scope plus all **20 NuGet** projects. The source report contains **0 HIGH/CRITICAL, 3 MODERATE per npm scope**, with six warning records (each package appears in both scopes); NuGet reported zero advisories at that observation. This is a historical source snapshot, not a standing zero-vulnerability claim.

| npm package/version | Finding | Locked relationship | Severity / fix |
|---|---|---|---|
| `minimatch@10.2.6` | `GHSA-g3hv-75wg-m249`, CVE-2026-118764 | Transitive through `expo@58.0.26` → `@expo/fingerprint@0.21.10` → `minimatch@10.2.6` | MODERATE; upstream fixed in minimatch 11.0.1 |
| `@expo/fingerprint@0.21.10` | `npm:indirect:@expo/fingerprint` | Expo Node native fingerprint/build tooling, depends on minimatch `^10.2.6` | MODERATE npm meta-vulnerability; no separate GHSA |
| `expo@58.0.26` | `npm:indirect:expo` | Direct app dependency that includes the above Node tooling | MODERATE npm meta-vulnerability; no separate GHSA |

The vulnerability is a possible CPU/memory exhaustion from pathological *glob patterns* with `optimizationLevel >= 2`. It is not demonstrated to be reachable from Omnia's note capture or authenticated API. The npm production install classification does not prove that the affected Node tooling ships in the browser/native app runtime. The Expo upstream maintainers assessed this case as tool-only and retained minimatch 10 in fingerprint due to breaking API changes; this **does not prove** absence of risk in every CI/build scenario. An automatic minimatch 11 override could break Expo's runtime/parser integrations and is prohibited.

Official sources:
- https://github.com/advisories/GHSA-g3hv-75wg-m249
- https://github.com/expo/expo/issues/52735
- https://docs.expo.dev/versions/v58.0.0/sdk/fingerprint/

## Mandatory scoped review

`security/moderate-advisory-reviews.json` contains only exact `(package,version,advisory)` identities, explicit `all` and `production` scopes, a GHSA root reference, upstream link, human-readable risk/mitigation and the GitHub owner / issue #196. The review date is **2026-11-24**, due **2026-12-08 UTC** (14 days). It is NOT a waiver, override, vulnerability fix or suppressed warning; the canonical npm audit JSON retains all six warning records.

CI runs `python3 -m unittest discover -s tests/quality -p test_moderate_reviews.py`, invokes `run_live_advisory_audit.py` unchanged, and then (only when the live scan is complete) runs:

```sh
python3 tests/quality/verify_moderate_reviews.py \
  --report "$RUNNER_TEMP/omnia-security-audit/summary.json" \
  --reviews security/moderate-advisory-reviews.json \
  --source-sha "$GITHUB_SHA"
```

The verifier rejects any **new, missing, differently scoped or changed warning**, stale/mismatched SHA, missing NuGet source audits, missing/duplicate review, expired review, broad wildcard, short risk justification, or unsupported `waived` fields. It never modifies the findings. If npm adds a moderate advisory or Expo changes versions, a human must inspect the official report and update the registry deliberately; **no silent acceptance**. On or after **2026-12-08 UTC** the review expires and CI intentionally becomes red until a new time-bounded assessment is made.

## Independent obligations and limits

This tool guards the *review workflow*, not exploitability, CVE scanning, native parity or release readiness. The existing high/critical advisory gate remains fail-closed; supply-chain checks and EAS/native tests stay mandatory. A pass under this scoped risk register is not evidence that dependencies are fully patched or that a GDPR launch is approved. Parent #196 stays OPEN until a compatible upstream fix or a separately validated release decision, and #22/#15/#10 remain OPEN.

### Scope of review ownership

The owner field assigns **accountability for a follow-up review**; creating this registry does **not** record a human security sign-off, formal risk acceptance, or authorization to deploy. Technical reachability notes are provisional and must be revisited with the project owner by the expiry date. The gate validates the presence and freshness of documentation, not the truth of a threat-model claim.
