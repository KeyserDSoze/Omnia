# ADR 0091 — Coordinated Expo SDK58 security migration, static Pages and React Native ABI

- Date: 2026-11-23
- Status: accepted for dependency graph and static/Metro validation; device release evidence pending (#195)
- Issues: #195 SDK/Router migration and native gate; #194 npm HIGH/CRITICAL remediation; #193 SCA
- Parents: #22 supply chain; #15 reproducibility; #10 quality (remain open)
- Revises Expo SDK57 version matrix from ADR 0085, retaining npm v3 lock policy

## Context

A read-only 2026-11-23 npm audit of the SDK57 lock reported
two CRITICAL through `vitest@3.2.7`/`tinypool@1.1.1`,
three HIGH through `expo-router@57.0.32`,
`query-string@7.1.3` and `decode-uri-component@0.2.2`,
plus four MODERATE package findings. The genuine CommonJS runtime
test rejects an attempted override to ESM-only
`decode-uri-component@0.5.1` because it throws
`TypeError: decodeComponent is not a function`. Never bypass npm
peer resolution with `--force`, `--legacy-peer-deps` or unreviewed
transitive overrides.

## Decision and real evidence

1. First migrate Vitest to **5.1.1**, removing the vulnerable
   tinypool test dependency. [Trial #45215428007](https://github.com/KeyserDSoze/Tao/actions/runs/45215428007)
   and commit [d6b9af2](https://github.com/KeyserDSoze/Tao/commit/d6b9af290)
   confirm this separately.
2. Migrate the entire Expo stack using SDK58 and the official
   `npx expo install --fix --npm` matrix (not an arbitrary Router
   major override). Lock resolves **expo@58.0.26**,
   **expo-router@58.0.32**, **react@19.3.0**,
   **react-native@0.88.1**, Reanimated **4.7.1**, Worklets
   **0.13.0**, Gesture Handler **3.3.0**, and Vitest **5.1.1**.
   Preserve exact npm v3 root ranges and SHA-512 tarball integrity.
3. The [read-only trial #45217840306](https://github.com/KeyserDSoze/Tao/actions/runs/45217840306)
   successfully exercised clean npm ci, Expo version reconciliation,
   TypeScript, Vitest, static Expo Web export, Playwright static E2E
   **2/2**, Metro Android bundle and npm audit both full and
   production (**0 HIGH/CRITICAL; 3 MODERATE**). It verified
   `query-string` and `decode-uri-component` are absent from the
   resolved graph. The [checked nonforce promotion #45218562795](https://github.com/KeyserDSoze/Tao/actions/runs/45218562795)
   repeated all gates before committing ONLY package.json and
   package-lock.json on main at
   [e0ebb14](https://github.com/KeyserDSoze/Tao/commit/e0ebb14acbbf0152b3d9eeb0acd75c6c0a98846c).
4. Explicitly preserve `src/mobile/app.json` with
   `web.output=static` and `experiments.baseUrl=/Tao`
   for GitHub Pages, because SDK58 Router may otherwise select
   server rendering; Pages never hosts the .NET API.
5. TDD RED [CI #45219863627](https://github.com/KeyserDSoze/Tao/actions/runs/45219863627)
   showed five failures against an empty policy for old SDK,
   vulnerable nested package names, changed native ABI, unsafe
   overrides and Pages server output. GREEN implemented
   `sdk58-security-policy.mjs` with six positive/negative unit
   tests and `verify-sdk58-security.mjs`, invoked before npm ci
   from CI/OIDC/Pages/native. [CI #45220400873](https://github.com/KeyserDSoze/Tao/actions/runs/45220400873)
   passed the policy, Metro Android **and iOS** bundles and the
   separate live npm+NuGet SCA job (**20/20 NuGet**). Real OIDC and
   Pages jobs also passed the same SHA.
6. Preserve independent npm and NuGet advisory scanning in CI.
   A later CVE, malformed report or registry outage blocks the
   security gate. **Three MODERATE** findings remain to be reviewed;
   no claim of zero vulnerabilities or 100% supply-chain security.

## Residual risks and planned follow-up

- Metro Android/iOS bundles on Ubuntu are not complete signed EAS
  builds, native module loading, simulator/runtime smoke or
  store credentials. #195 stays OPEN for those gates.
- Native SDK/runtime dependencies, pgvector/Keycloak image CVEs,
  SBOM of built binaries, licenses, SAST, provenance/attestation,
  Azure deployments and GDPR still require independent evidence.
  #22/#15/#10 remain OPEN.
- Re-evaluate npm all+production and NuGet advisories continuously.
  Upgrade SDK/native/version locks together under a tracked issue;
  never silently unpin or loosen offline security regression tests.

Official Expo references:
- https://expo.dev/changelog/sdk-58
- https://docs.expo.dev/versions/latest/
- https://docs.expo.dev/router/introduction/
