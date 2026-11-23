# Expo SDK58 / Router58 security migration and native follow-up

See [ADR 0091](../adr/0091-expo-sdk58-security-migration.md),
[#195](https://github.com/KeyserDSoze/Omnia/issues/195) and
[ADR 0090](../adr/0090-fail-closed-npm-nuget-advisory-gate.md).

## Reproducible source and npm verification

```sh
cd src/mobile
node --test scripts/mobile-lock-policy.test.mjs scripts/sdk58-security-policy.test.mjs
node scripts/verify-mobile-lockfile.mjs
node scripts/verify-sdk58-security.mjs
npm ci
node --test scripts/query-string-compatibility.test.mjs
npm run typecheck
npm test
npm run build:web
npx playwright install --with-deps chromium
npm run test:e2e
npx expo export --platform android
npx expo export --platform ios
npm audit --json --audit-level=high
npm audit --json --omit=dev --audit-level=high
```

The two npm audit commands must be parsed/checked by the live
`security-audit` CI job; npm exit 1 can mean real findings,
while feed outages, invalid JSON and unknown scanner statuses
are fail-closed. CI scans all twenty NuGet graphs as well.
Do not add an exception just to make CI pass. Changes require
issue, scoped risk mitigation, owner, advisory ID and expiry.
Current exceptions list is empty.

## Platform boundaries and security remediation

- Lock expected SDK58 resolving Expo 58.0.26, Router 58.0.32,
  RN 0.88.1, React 19.3.0, Vitest 5.1.1. No forced overrides.
- Expo Router defaults must NOT replace
  `app.json web.output=static` and `/Omnia` Pages base URL.
- Never restore the vulnerable `query-string`,
  `decode-uri-component` or `tinypool` graph via nested packages.
  The ESM-only decoder override failed a genuine CommonJS test.
- CI produces Metro Android/iOS bundles. These prove bundling,
  **not** APK/IPA native compilation, signability, store delivery,
  simulator smoke, real device permissions or offline auth.
- The opt-in EAS workflow is gated by credentials/enablement.
  Track tests for Android/iOS native OIDC/PKCE redirects, secure
  storage, camera/mic permissions, audio and consent before
  claiming product readiness. Publish run IDs and credentials-
  safe evidence to #195.
- Three npm MODERATE advisories remained in the trial even
  though the fail-closed HIGH/CRITICAL policy passed. Review
  each before release, and keep #22/#15/#10 open.

## Change procedure

1. Open an issue and check official Expo SDK/Router, RN and
   native module compatibility. Compare recent CVE advisories.
2. On an isolated runner, generate official npm lock without
   `--force`, inspect the changed complete package graph,
   confirm `npm ci` and the offline SDK security contract.
3. Run real Web/Android/iOS export tests, genuine OIDC on
   Keycloak/PostgreSQL, and live SCA for npm and NuGet. For
   release, add signed EAS builds and simulator/device runs.
4. Publish only validated package manifest/lock changes on
   main without force, add ADR/docs/tests and verify same-SHA
   CI, real OIDC and Pages. Do not close native gate #195
   without native evidence.
