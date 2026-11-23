# Expo/npm dependency lock runbook (#188, ADR 0085)

This repository's `src/mobile/package-lock.json` is a reviewed npm v3
dependency graph produced on a clean Node 22/npm 10 GitHub runner,
then verified by a successful `npm ci`, TypeScript, Vitest and web export.

## CI policy

- Every Web/CI/OIDC (and gated manual native) job validates the immutable
  lock with `node scripts/verify-mobile-lockfile.mjs` from `src/mobile`.
- Every install uses `npm ci --no-audit --no-fund`. An absent or
  manifest-inconsistent lock must abort; do not fall back to `npm install`
  or disable integrity.
- `actions/setup-node` caches the npm download cache, keyed to the
  committed lockfile, not the resulting `node_modules` tree.
- The lock validator checks exact root package dependency ranges,
  npm registry HTTPS tarball origins, and SHA-512 integrity. No third
  party registry or local symlink is allowed in the locked graph.
- Expo SDK 57 requires the compatible native peer trio from the official
  template: Reanimated 4.5.1, Worklets 0.10.1, Gesture Handler ~2.32.0;
  manual native device builds are NOT yet verified.

## Intentional upgrade procedure

1. Review Expo SDK/RN peer compatibility and official package advisories.
2. Update `src/mobile/package.json` deliberately. On a clean Node 22
   environment, generate the lock from the official registry:
   `npm install --package-lock-only --ignore-scripts --no-audit --no-fund`.
3. Run `node scripts/verify-mobile-lockfile.mjs` followed by
   `npm ci --no-audit --no-fund`, `npm run typecheck`, `npm test`,
   `npm run build:web`, and platform-relevant smoke checks.
4. Review `package-lock.json` changes and provenance; do not commit
   opaque transitive changes, bypass peer conflicts with
   `--legacy-peer-deps`/`--force`, or inject credentials into
   npm logs/cache.
5. Track the modification with an issue and, if architectural,
   an ADR; verify CI/Pages/OIDC success on the same final SHA.

A locked graph remains dependent on external npm registry availability.
The prior tarball 404 is not proof of compromise and also is not
permanently fixed merely by a lockfile. NuGet packages.lock, SBOM/SCA,
full SDK pinning and native smoke are separate gates under #15/#10.

References: [ADR 0085](../adr/0085-locked-expo-npm-dependency-graph.md),
[issue #188](https://github.com/KeyserDSoze/Omnia/issues/188).
