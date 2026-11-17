import { test, expect } from '@playwright/test';

// This suite must only run against the real OIDC provider, ASP.NET Core API and
// disposable pgvector PostgreSQL, never mocked network routes or test-only JWTs.
test.skip(process.env.OMNIA_REAL_OIDC_E2E !== '1', 'Run via the real OIDC CI job.');
const user1 = process.env.OMNIA_E2E_USER1 ?? '';
const user2 = process.env.OMNIA_E2E_USER2 ?? '';
const password = process.env.OMNIA_E2E_PASSWORD ?? '';

async function browserLogin(page: import('@playwright/test').Page, username: string) {
  await expect(page.getByTestId('auth-login')).toBeVisible({ timeout: 30_000 });
  const outgoing = page.waitForRequest(req =>
    req.url().includes('/protocol/openid-connect/auth?'), { timeout: 30_000 });
  await page.getByTestId('auth-login').click();
  const request = new URL((await outgoing).url());
  expect(request.searchParams.get('response_type')).toBe('code');
  expect(request.searchParams.get('code_challenge_method')).toBe('S256');
  expect(request.searchParams.get('code_challenge')?.length).toBeGreaterThan(30);
  expect(request.searchParams.get('client_id')).toBe('omnia-web');
  await page.locator('input[name="username"]').fill(username);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('input[type="submit"]').click();
  await expect(page.getByTestId('auth-status')).toContainText('Sessione autenticata', { timeout: 45_000 });
  expect(new URL(page.url()).searchParams.has('code')).toBe(false);
  // Tokens must not land in browser persistent or session storage.
  const keys = await page.evaluate(() => ({
    local: Object.keys(localStorage),
    session: Object.keys(sessionStorage)
  }));
  expect(keys.local.join(' ')).not.toContain('oidc.user');
  expect(keys.session.join(' ')).not.toContain('oidc.user');
}

test('Keycloak PKCE -> JWT API -> PostgreSQL: consent, isolated sync, revoke', async ({
  page, browser, request
}) => {
  test.setTimeout(120_000);
  if (!user1 || !user2 || !password) throw new Error('Missing ephemeral OIDC test accounts');

  const unauthenticated = await request.get('http://127.0.0.1:8080/api/v1/memories');
  expect(unauthenticated.status()).toBe(401);

  await page.goto('/Omnia/');
  await browserLogin(page, user1);

  // Consent default-deny: no implicit grants at login.
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  await page.getByTestId('memo-input').fill('Riunione cliente E2E univoca');
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Autorizza prima');

  await page.getByTestId('consent-accept').click();
  await page.getByTestId('grant-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva', { timeout: 15_000 });

  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Nota salvata', { timeout: 15_000 });
  await expect(page.getByText('Riunione cliente E2E univoca', { exact: true })).toBeVisible();

  // Browser refresh does not persist bearer or refresh tokens: re-login via IdP
  // SSO session, then retrieve the already stored PostgreSQL entry.
  await page.reload();
  await browserLogin(page, user1);
  await expect(page.getByText('Riunione cliente E2E univoca', { exact: true })).toBeVisible();

  // Separate Keycloak user in a new browser context cannot read this user's notes
  // (tenant claim may be shared locally; owner sub must remain isolated).
  const second = await browser.newContext();
  try {
    const other = await second.newPage();
    await other.goto('/Omnia/');
    await browserLogin(other, user2);
    await expect(other.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
    await expect(other.getByText('Riunione cliente E2E univoca', { exact: true })).toHaveCount(0);
  } finally {
    await second.close();
  }

  await page.getByTestId('revoke-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  await page.getByTestId('memo-input').fill('Questo evento non deve apparire');
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Autorizza prima');
  await expect(page.getByText('Questo evento non deve apparire', { exact: true })).toHaveCount(0);
  await expect(page.getByText('Riunione cliente E2E univoca', { exact: true })).toBeVisible();
});
