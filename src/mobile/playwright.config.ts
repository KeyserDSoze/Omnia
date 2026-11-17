import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: process.env.CI ? 1 : 0,
  use: { baseURL: 'http://127.0.0.1:19006', ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  webServer: {
    command: 'node scripts/serve-static.mjs',
    url: 'http://127.0.0.1:19006/Omnia/',
    timeout: 30_000,
    reuseExistingServer: !process.env.CI
  }
});
