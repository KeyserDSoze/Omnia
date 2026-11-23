# ADR 0085 — Frozen Expo npm dependency graph on Node 22 / npm 10

- Date: 2026-11-23
- Status: accepted implementation on main; CI same-SHA run evidence is tracked in issue #188
- Related: quality epic #10, toolchain/lockfile child #15, npm regression #188
- Safety boundary: no changes to JWT, PostgreSQL RLS, identity, memory, consent or personal data

## Context

Without a committed npm lockfile, the Web and native dependency jobs
ran unrestricted `npm install`. The npm registry temporarily returned
404 for `@expo/inline-modules@0.1.10`. Re-resolution also selected
`react-native-reanimated@4.8.0` (peer supports RN 0.87+) and
`react-native-worklets@0.14.0`, inconsistent with the required RN 0.86.2.
The unmodified project once installed successfully, so a single green
build did not prove reproducibility.

## Decision

- Declare peer dependencies used by Expo Router explicitly per official
  Expo SDK 57 template: `react-native-reanimated=4.5.1`,
  `react-native-worklets=0.10.1`,
  `react-native-gesture-handler~2.32.0`.
  Do not force an incompatible transitive peer resolution.
- Generate the npm v3 lockfile on a disposable Node 22/npm 10 GitHub
  runner from the official registry, using no package scripts during
  metadata resolution. Before publishing, run the offline JSON policy,
  `npm ci`, TypeScript, Vitest and Expo static export, and inspect
  `npm ls` peer graph.
- Validate each root dependency against the checked-in manifest.
  For every resolved npm package require official
  `https://registry.npmjs.org/` tarball URL and SHA-512 integrity.
  Refuse missing lockfiles, mismatched versions or foreign URLs.
- All standard Web/CI/OIDC and gated native jobs use `npm ci` with the
  reviewed committed lockfile and npm cache keyed to its content.
  Do not use `npm install` during build/deploy or a permissive
  `--legacy-peer-deps` / `--force` fallback.
- Remove the privileged one-time lockfile bootstrap workflow after
  the lock is committed. It never owned production or personal data.

## TDD and evidence

RED commit 9cf6f36 adds 3 policy tests (all pass) and shows
CI mobile failure on missing package-lock (ENOENT) before install:
https://github.com/KeyserDSoze/Omnia/actions/runs/45190402898.

First bootstrap rejected npm ERESOLVE with incompatible latest optional
Reanimated peer:
https://github.com/KeyserDSoze/Omnia/actions/runs/45190701384.

Compatible peers and fresh `npm ci` then passed 61 Vitest, typecheck,
Expo export, but the first robot Git push lacked credentials:
https://github.com/KeyserDSoze/Omnia/actions/runs/45191248138.

Single-file scoped authenticated GitHub Contents API committed a tested
9,708-line lockfile on main commit 0ced034:
https://github.com/KeyserDSoze/Omnia/actions/runs/45191772525.

Same SHA success for CI/Pages/OIDC must be checked and linked in #188
before closure.

## Limitations

npm lockfiles freeze dependency resolution and integrity metadata but
do not guarantee a tarball remains published or that a registry/CDN is
always available. npm cache is not a hermetic mirror. No claim is made
about native Android/iOS signed builds, npm malware provenance attestation,
SCA/SBOM, NuGet deterministic locked restore, production security or GDPR
release readiness. Keep #15 and #10 OPEN.

Official sources:
- https://docs.npmjs.com/cli/npm-ci/
- https://github.com/actions/setup-node/blob/main/docs/advanced-usage.md
- https://docs.expo.dev/versions/v57.0.0/sdk/reanimated/
- https://github.com/expo/expo-template-default/blob/main/package.json
