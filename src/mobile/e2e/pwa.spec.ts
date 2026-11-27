import { expect, test } from '@playwright/test';

const base = (process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '');
const newer = {
  version: '9.0.0', build: 'feed1234abcd', builtAt: '2030-01-01T10:00:00Z',
  releases: [{ version: '9.0.0', date: '2030-01-01', notes: { en: ['Brand new things.'], it: ['Cose nuovissime.'] } }]
};

async function publishNewer(page: import('@playwright/test').Page) {
  await page.route('**/version.json*', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify(newer)
  }));
}

test('the app can be installed: manifest, icons and a version file are published', async ({ page, request }) => {
  await page.goto(base + '/');
  const href = await page.locator('link[rel="manifest"]').getAttribute('href');
  const manifest = await (await request.get(new URL(href!, page.url()).href)).json();
  expect(manifest.display).toBe('standalone');
  expect(manifest.start_url).toBeTruthy();
  const sizes = manifest.icons.map((i: { sizes: string }) => i.sizes);
  expect(sizes).toEqual(expect.arrayContaining(['192x192', '512x512']));
  const version = await (await request.get(new URL('version.json', page.url()).href)).json();
  expect(version.version).toMatch(/^\d+\.\d+\.\d+$/);
  expect(version.build).toMatch(/^[a-f0-9]{6,64}$/);
  expect(version.releases[0].version).toBe(version.version);
});

test('after the first visit the app opens without a connection', async ({ page, context }) => {
  await page.goto(base + '/');
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.reload();
  await expect.poll(() => page.evaluate(() => navigator.serviceWorker.controller !== null)).toBe(true);
  await context.setOffline(true);
  await page.reload();
  await expect(page.getByTestId('open-settings')).toBeVisible({ timeout: 15_000 });
  await context.setOffline(false);
});

test('Settings shows the version and what is new', async ({ page }) => {
  await page.goto(base + '/');
  await page.getByTestId('open-settings').click();
  await expect(page.getByTestId('app-version')).toContainText(/Tao \d+\.\d+\.\d+/);
  await expect(page.getByTestId('app-update-mode')).toContainText('chiede prima');
});

test('a new version is announced with its notes and can be postponed', async ({ page }) => {
  await publishNewer(page);
  await page.goto(base + '/');
  const banner = page.getByTestId('update-banner');
  await expect(banner).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('update-version')).toContainText('9.0.0');
  await expect(banner).toContainText('Cose nuovissime.');
  await page.getByTestId('update-later').click();
  await expect(banner).toBeHidden();
  await page.reload();
  await expect(page.getByTestId('open-settings')).toBeVisible();
  await expect(banner).toBeHidden();
});

test('"don\'t ask again" closes the prompt for good and updates quietly from then on', async ({ page }) => {
  await publishNewer(page);
  await page.goto(base + '/');
  await expect(page.getByTestId('update-banner')).toBeVisible({ timeout: 15_000 });
  await page.getByTestId('update-never').click();
  await expect(page.getByTestId('update-banner')).toBeHidden();
  expect(await page.evaluate(() => localStorage.getItem('tao.update.preference'))).toBe('silent');
  // A new session: no prompt, and the choice can be undone from Settings.
  await page.evaluate(() => sessionStorage.clear());
  await page.reload();
  await expect(page.getByTestId('open-settings')).toBeVisible();
  await expect(page.getByTestId('update-banner')).toBeHidden();
  await page.getByTestId('open-settings').click();
  await expect(page.getByTestId('app-update-mode')).toContainText('in silenzio');
  await page.getByTestId('app-update-preference').click();
  expect(await page.evaluate(() => localStorage.getItem('tao.update.preference'))).toBe('ask');
});

test('Settings checks for updates on request', async ({ page }) => {
  await page.goto(base + '/');
  await page.getByTestId('open-settings').click();
  await page.getByTestId('app-update-check').click();
  await expect(page.getByTestId('app-update-outcome')).toContainText('ultima versione');
});
