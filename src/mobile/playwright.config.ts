import { defineConfig, devices } from '@playwright/test';
// Must match the export: '' (gateway) or '/Tao' (GitHub Pages and the OIDC E2E job).
const base = (process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '');
// TAO_E2E_BASE_URL targets an already running stack (e.g. the on-prem gateway).
const external = process.env.TAO_E2E_BASE_URL;
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: process.env.CI ? 1 : 0,
  use: {
    baseURL: external ?? 'http://127.0.0.1:19006',
    ...devices['Desktop Chrome'],
    // The specs read the Italian texts; the app follows the browser language.
    locale: 'it-IT',
    trace: 'retain-on-failure'
  },
  webServer: external ? undefined : {
    command: 'node scripts/serve-static.mjs',
    url: `http://127.0.0.1:19006${base}/`,
    timeout: 30_000,
    reuseExistingServer: !process.env.CI
  }
});
