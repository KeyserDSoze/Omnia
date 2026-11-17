# Browser E2E tests

Tests live in `src/mobile/e2e/` beside the Expo package so Playwright resolves its pinned dependencies reliably. Run `cd src/mobile && npm run test:e2e`. The CI invokes a real Chromium browser against the static Expo export at `/Omnia/`.
