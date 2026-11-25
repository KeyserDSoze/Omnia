import { test, expect, type Page } from '@playwright/test';

// Runs only against the disposable stack of docker/keycloak/e2e/social-e2e.ps1: the real Tao realm,
// first-login flow and API, with a synthetic OIDC upstream standing in for Google and Microsoft.
test.skip(process.env.TAO_SOCIAL_E2E !== '1', 'Run via docker/keycloak/e2e/social-e2e.ps1.');
test.describe.configure({ mode: 'serial' });

const kc = process.env.TAO_E2E_KC_URL ?? 'http://localhost:8081';
const adminPassword = process.env.TAO_E2E_KC_PASSWORD ?? '';
const upstreamPassword = process.env.TAO_E2E_PASSWORD ?? '';
const appPath = `${(process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '')}/`;

async function admin<T>(path: string): Promise<T> {
  const token = await fetch(`${kc}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    body: new URLSearchParams({
      grant_type: 'password', client_id: 'admin-cli', username: 'ci-admin', password: adminPassword
    })
  }).then(r => r.json() as Promise<{ access_token: string }>);
  const response = await fetch(`${kc}/admin/realms/tao/${path}`, {
    headers: { authorization: `Bearer ${token.access_token}` }
  });
  expect(response.ok).toBe(true);
  return await response.json() as T;
}

/** Tao users with this e-mail and the providers linked to each. */
async function taoUsers(email: string) {
  const users = await admin<{ id: string; email: string; emailVerified: boolean }[]>(
    `users?email=${encodeURIComponent(email)}&exact=true`);
  return Promise.all(users.map(async user => ({
    ...user,
    providers: (await admin<{ identityProvider: string }[]>(`users/${user.id}/federated-identity`))
      .map(identity => identity.identityProvider).sort()
  })));
}

async function signInWith(page: Page, provider: 'google' | 'microsoft', email: string) {
  await page.goto(appPath);
  await page.getByTestId(`chat-login-${provider}`).click();
  await page.locator('input[name="username"]').fill(email);
  await page.locator('input[name="password"]').fill(upstreamPassword);
  await page.locator('#kc-login').click();
}

const signedIn = (page: Page) => expect(page.getByTestId('chat-input')).toBeVisible({ timeout: 45_000 });

test('the first Google sign-in creates the Tao user from the e-mail and the next one finds it', async ({ page, context }) => {
  expect(await taoUsers('ada@example.test')).toEqual([]);
  await signInWith(page, 'google', 'ada@example.test');
  await signedIn(page);
  const [created, ...others] = await taoUsers('ada@example.test');
  expect(others).toEqual([]);
  expect(created.providers).toEqual(['google']);

  await context.clearCookies();
  await page.evaluate(() => { localStorage.clear(); sessionStorage.clear(); });
  await signInWith(page, 'google', 'ada@example.test');
  await signedIn(page);
  const again = await taoUsers('ada@example.test');
  expect(again.map(user => user.id)).toEqual([created.id]);
});

test('Microsoft with an e-mail that already has a Tao user joins it only after proof of the first provider', async ({ page }) => {
  await signInWith(page, 'microsoft', 'ada@example.test');
  // No silent link: Keycloak asks to confirm, and nothing changed meanwhile.
  await page.locator('#linkAccount').click();
  await expect(page.getByTestId('chat-input')).toHaveCount(0);
  expect((await taoUsers('ada@example.test'))[0].providers).toEqual(['google']);
  // Proof: re-authenticate through the provider that is already linked.
  await page.locator('#social-google').click();
  await signedIn(page);
  const users = await taoUsers('ada@example.test');
  expect(users).toHaveLength(1);
  expect(users[0].providers).toEqual(['google', 'microsoft']);
});

test('the Tao realm has no password form of its own', async ({ page }) => {
  const redirect = encodeURIComponent(`http://127.0.0.1:19006${appPath}`);
  await page.goto(`${kc}/realms/tao/protocol/openid-connect/auth?client_id=tao-web&response_type=code&scope=openid&redirect_uri=${redirect}`);
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  await expect(page.locator('input[name="username"]')).toHaveCount(0);
  const grant = await fetch(`${kc}/realms/tao/protocol/openid-connect/token`, {
    method: 'POST',
    body: new URLSearchParams({ grant_type: 'password', client_id: 'tao-web', username: 'ada@example.test', password: upstreamPassword })
  });
  expect(grant.ok).toBe(false);
});

test('a new e-mail through Microsoft creates its own, separate Tao user', async ({ page }) => {
  await signInWith(page, 'microsoft', 'grace@example.test');
  await signedIn(page);
  const users = await taoUsers('grace@example.test');
  expect(users).toHaveLength(1);
  expect(users[0].providers).toEqual(['microsoft']);
  expect((await taoUsers('ada@example.test'))).toHaveLength(1);
});
