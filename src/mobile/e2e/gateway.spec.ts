import { test, expect } from '@playwright/test';

// Runs only against the on-prem docker compose stack (nginx :59800 -> web, /api, /auth).
test.skip(process.env.TAO_GATEWAY_E2E !== '1', 'Run via the gateway CI job.');
const username = process.env.TAO_E2E_USER1 ?? '';
const password = process.env.TAO_E2E_PASSWORD ?? '';

test('single port: Keycloak /auth login, consent and note through /api', async ({ page }) => {
  test.setTimeout(90_000);
  if (!username || !password) throw new Error('Missing disposable gateway account');

  await page.goto('/');
  await page.getByTestId('auth-login').click();
  // Same-origin OIDC: the authorization request never leaves the gateway origin.
  await expect(page).toHaveURL(/^http:\/\/localhost:59800\/auth\/realms\/tao\//);
  await page.locator('input[name="username"]').fill(username);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('#kc-login').click();
  await expect(page.getByTestId('auth-status')).toContainText('Sessione autenticata', { timeout: 45_000 });
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');

  await page.getByTestId('consent-accept').click();
  await page.getByTestId('grant-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva', { timeout: 15_000 });

  const memo = 'Nota gateway ' + Date.now();
  await page.getByTestId('memo-input').fill(memo);
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Nota salvata', { timeout: 15_000 });
  await expect(page.getByText(memo, { exact: true })).toBeVisible();
});
