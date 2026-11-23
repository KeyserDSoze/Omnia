import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

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
  // After the first sign-in Keycloak may reuse the IdP SSO cookie and skip the form.
  const usernameInput = page.locator('input[name="username"]');
  let loginFormVisible = false;
  try {
    await usernameInput.waitFor({ state: 'visible', timeout: 6_000 });
    loginFormVisible = true;
  } catch {
    // The IdP might have auto-redirected an existing SSO session.
  }
  if (loginFormVisible) {
    await usernameInput.fill(username);
    await page.locator('input[name="password"]').fill(password);
    // Keycloak 26 renders an actual <button>, not <input type="submit">.
    // Never swallow failures from clicking or submitting the real form.
    await page.locator('#kc-login').click();
  }
  await expect(page.getByTestId('auth-status')).toContainText('Sessione autenticata', { timeout: 45_000 });
  // A successful OIDC callback is not sufficient: the API must accept the
  // signed access token with unique sub/tenant_id/aud before we proceed.
  await expect(page.getByTestId('consent-state')).toBeVisible({ timeout: 15_000 });
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
  expect((await request.get('http://127.0.0.1:8080/api/v1/privacy/export')).status()).toBe(401);
  expect((await request.post('http://127.0.0.1:8080/api/v1/privacy/erase',
    { data: { confirmation: 'ERASE_MY_DATA' } })).status()).toBe(401);

  await page.goto('/Omnia/');
  await browserLogin(page, user1);

  // Consent default-deny: no implicit grants at login.
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  const memoText = 'Riunione cliente E2E ' + Date.now();
  await page.getByTestId('memo-input').fill(memoText);
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Autorizza prima');

  await page.getByTestId('consent-accept').click();
  await page.getByTestId('grant-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva', { timeout: 15_000 });

  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Nota salvata', { timeout: 15_000 });
  await expect(page.getByText(memoText, { exact: true })).toBeVisible();

  // Browser refresh does not persist bearer or refresh tokens: re-login via IdP
  // SSO session, then retrieve the already stored PostgreSQL entry.
  await page.reload();
  await browserLogin(page, user1);
  await expect(page.getByText(memoText, { exact: true })).toBeVisible();

  // Separate Keycloak user in a new browser context cannot read this user's notes
  // (tenant claim may be shared locally; owner sub must remain isolated).
  const otherMemo = 'Second account E2E ' + Date.now();
  const second = await browser.newContext();
  try {
    const other = await second.newPage();
    await other.goto('http://127.0.0.1:19006/Omnia/');
    await browserLogin(other, user2);
    await expect(other.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
    await expect(other.getByText(memoText, { exact: true })).toHaveCount(0);
    await other.getByTestId('consent-accept').click();
    await other.getByTestId('grant-consent').click();
    await expect(other.getByTestId('consent-state')).toContainText('Autorizzazione attiva');
    await other.getByTestId('memo-input').fill(otherMemo);
    await other.getByTestId('save-memo').click();
    await expect(other.getByTestId('notice')).toContainText('Nota salvata');
    await expect(other.getByTestId('memory-timeline').getByText(otherMemo)).toBeVisible();
  } finally {
    await second.close();
  }

  await page.getByTestId('revoke-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  await page.getByTestId('memo-input').fill('Questo evento non deve apparire');
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Autorizza prima');
  // The denied draft correctly stays in the text editor. Assert against
  // the persisted timeline only, then reload it from PostgreSQL to prove
  // that revocation blocked a write instead of merely hiding an item.
  const reloadedFromApi = page.waitForResponse(response =>
    response.url().includes('/api/v1/memories/page?') && response.status() === 200);
  await page.getByTestId('sync-reload').click();
  await reloadedFromApi;
  await expect(page.getByTestId('memory-timeline').getByText(
    'Questo evento non deve apparire', { exact: true })).toHaveCount(0);
  await expect(page.getByText(memoText, { exact: true })).toBeVisible();

  // Export after revocation must still include historical note, its raw and
  // normalized content and all first-party grants, never another owner's data.
  const [firstDownload] = await Promise.all([
    page.waitForEvent('download', { timeout: 20_000 }),
    page.getByTestId('export-data').click()
  ]);
  expect(firstDownload.suggestedFilename()).toBe('omnia-personal-data.json');
  const firstExport = JSON.parse(await readFile(await firstDownload.path(), 'utf8'));
  expect(firstExport.scope).toBe('omnia-postgresql-primary');
  expect(firstExport.memoryEntries.some((row: { rawContent: string }) =>
    row.rawContent === memoText)).toBe(true);
  expect(firstExport.memoryEntries.some((row: { rawContent: string }) =>
    row.rawContent === otherMemo)).toBe(false);
  expect(firstExport.consentGrants.length).toBeGreaterThanOrEqual(1);

  // Deliberate two-action erasure acknowledgement. A wrong phrase remains disabled.
  await expect(page.getByTestId('erase-data')).toBeDisabled();
  await page.getByTestId('erase-acknowledge').click();
  await page.getByTestId('erase-confirmation').fill('elimina');
  await expect(page.getByTestId('erase-data')).toBeDisabled();
  await page.getByTestId('erase-confirmation').fill('ELIMINA');
  await page.getByTestId('erase-data').click();
  await expect(page.getByTestId('notice')).toContainText('database principale confermata', {
    timeout: 15_000
  });
  await expect(page.getByTestId('memory-timeline').getByText(memoText)).toHaveCount(0);
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');

  const afterErasure = page.waitForResponse(response =>
    response.url().includes('/api/v1/memories/page?') && response.status() === 200);
  await page.getByTestId('sync-reload').click();
  await afterErasure;
  await expect(page.getByTestId('memory-timeline').getByText(memoText)).toHaveCount(0);

  const [secondDownload] = await Promise.all([
    page.waitForEvent('download', { timeout: 20_000 }),
    page.getByTestId('export-data').click()
  ]);
  const emptyExport = JSON.parse(await readFile(await secondDownload.path(), 'utf8'));
  expect(emptyExport.memoryEntries).toEqual([]);
  expect(emptyExport.consentGrants).toEqual([]);

  // Erasing alice must never affect bob's original PostgreSQL rows.
  const after = await browser.newContext();
  try {
    const other = await after.newPage();
    await other.goto('http://127.0.0.1:19006/Omnia/');
    await browserLogin(other, user2);
    await expect(other.getByTestId('memory-timeline').getByText(otherMemo)).toBeVisible();
  } finally {
    await after.close();
  }
});


test('logout does not re-display a real PostgreSQL note from an in-flight refresh', async ({
  page
}) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC test account');

  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  await page.getByTestId('consent-accept').click();
  await page.getByTestId('grant-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  const privateNote = 'Nota privata logout race ' + Date.now();
  await page.getByTestId('memo-input').fill(privateNote);
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('memory-timeline').getByText(privateNote)).toBeVisible();

  // RED: this is a real API response, from genuine Keycloak-signed bearer and
  // PostgreSQL. The test only delays delivery of its authentic bytes; no
  // synthetic JWT, fake response or mocked route result is created.
  let deliver!: () => void;
  let received!: () => void;
  const hold = new Promise<void>(resolve => { deliver = resolve; });
  const upstreamReceived = new Promise<void>(resolve => { received = resolve; });
  await page.route('**/api/v1/memories/page?*', async route => {
    const upstream = await route.fetch();
    if (upstream.status() !== 200) throw new Error('Real API failed during race test');
    received();
    await hold;
    await route.fulfill({ response: upstream });
  });

  const lateResponse = page.waitForResponse(response =>
    response.url().includes('/api/v1/memories/page?') && response.status() === 200);
  await page.getByTestId('sync-reload').click();
  await upstreamReceived;
  await page.getByTestId('auth-logout').click();
  await expect(page.getByTestId('auth-login')).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(privateNote)).toHaveCount(0);

  deliver();
  await lateResponse;
  // The pre-fix refresh unconditionally restores the old session's content.
  await expect(page.getByTestId('memory-timeline').getByText(privateNote)).toHaveCount(0);
  await expect(page.getByTestId('consent-state')).toHaveCount(0);
  const browserStores = await page.evaluate(() => ({
    local: Object.keys(localStorage), session: Object.keys(sessionStorage)
  }));
  expect(browserStores.local.join(' ')).not.toContain('oidc.user');
  expect(browserStores.session.join(' ')).not.toContain('oidc.user');
});


test('real API 401 clears the signed-in browser timeline and consent', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC test account');

  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  // Earlier real-browser scenarios may have left this synthetic user's
  // consent active in the same disposable PostgreSQL test instance.
  if (await page.getByTestId('consent-accept').count() > 0) {
    await page.getByTestId('consent-accept').click();
    await page.getByTestId('grant-consent').click();
  }
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  const privateNote = 'Nota privata su 401 ' + Date.now();
  await page.getByTestId('memo-input').fill(privateNote);
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('memory-timeline').getByText(privateNote)).toBeVisible();

  // Genuine ASP.NET JwtBearer authentication challenge. Playwright changes
  // only one outgoing request header; it does NOT forge the response, access
  // token or PostgreSQL data. No bearer => real API answers 401.
  await page.route('**/api/v1/memories/page?*', async route => {
    const headers = { ...route.request().headers() };
    delete headers.authorization;
    await route.continue({ headers });
  });
  const rejected = page.waitForResponse(response =>
    response.url().includes('/api/v1/memories/page?') && response.status() === 401);
  await page.getByTestId('sync-reload').click();
  await rejected;

  // RED #171: old UI merely shows "Impossibile aggiornare timeline", leaving
  // the previous signed-in user and personal note visible after API 401.
  await expect(page.getByTestId('auth-login')).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(privateNote)).toHaveCount(0);
  await expect(page.getByTestId('consent-state')).toHaveCount(0);
  await expect(page.getByTestId('notice')).toContainText('Sessione non valida');
  const stores = await page.evaluate(() => ({
    local: Object.keys(localStorage), session: Object.keys(sessionStorage)
  }));
  expect(stores.local.join(' ')).not.toContain('oidc.user');
  expect(stores.session.join(' ')).not.toContain('oidc.user');
});


test('logout while real PostgreSQL capture POST is awaiting its response', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC test account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  // The real synthetic account can retain consent across preceding E2E cases.
  if (await page.getByTestId('consent-accept').count() > 0) {
    await page.getByTestId('consent-accept').click();
    await page.getByTestId('grant-consent').click();
  }
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  const memo = 'Nota POST pendente al logout ' + Date.now();
  let release!: () => void;
  let received!: () => void;
  const hold = new Promise<void>(resolve => { release = resolve; });
  const accepted = new Promise<void>(resolve => { received = resolve; });
  let startedAfterLogout = 0;
  let loggedOut = false;
  await page.route('**/api/v1/memories/page?*', async route => {
    if (loggedOut) startedAfterLogout++;
    await route.continue();
  });
  await page.route('**/api/v1/memories', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    // Real signed OIDC bearer, real endpoint and PostgreSQL commit. Only
    // delivery of the genuine accepted response is deferred, never faked.
    const server = await route.fetch();
    // Omnia's capture contract returns 200 OK with canonical row identity.
    expect(server.status()).toBe(200);
    received();
    await hold;
    await route.fulfill({ response: server });
  });

  try {
    await page.getByTestId('memo-input').fill(memo);
    await page.getByTestId('save-memo').click();
    await accepted;
    // RED: pre-fix logout() returns early when busy=true.
    await page.getByTestId('auth-logout').click();
    await expect(page.getByTestId('auth-login')).toBeVisible();
    loggedOut = true;
    await expect(page.getByTestId('consent-state')).toHaveCount(0);
    await expect(page.getByTestId('memo-input')).toHaveValue('');
    await expect(page.getByTestId('memory-timeline').getByText(memo)).toHaveCount(0);
    await expect(page.getByTestId('notice')).toContainText('potrebbe essere');

    release();
    await expect(page.getByTestId('auth-login')).toBeVisible();
    await expect(page.getByTestId('memory-timeline').getByText(memo)).toHaveCount(0);
    await expect(page.getByTestId('notice')).not.toContainText('Nota salvata nel cloud');
    expect(startedAfterLogout).toBe(0);

    // The server already accepted the POST: logout must not pretend it was
    // rolled back. On fresh OIDC login, the PostgreSQL row must be visible.
    await page.unroute('**/api/v1/memories');
    await browserLogin(page, user1);
    await expect(page.getByTestId('memory-timeline').getByText(memo)).toBeVisible();
  } finally {
    release();
  }
});

test('logout while genuine private export is in flight prevents later download', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC test account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);

  let release!: () => void;
  let received!: () => void;
  const hold = new Promise<void>(resolve => { release = resolve; });
  const accepted = new Promise<void>(resolve => { received = resolve; });
  const downloads: string[] = [];
  page.on('download', download => downloads.push(download.suggestedFilename()));
  await page.route('**/api/v1/privacy/export', async route => {
    const server = await route.fetch();
    expect(server.status()).toBe(200);
    received();
    await hold;
    await route.fulfill({ response: server });
  });

  try {
    await page.getByTestId('export-data').click();
    await accepted;
    // RED: export sets busy, which previously made Logout a silent no-op.
    await page.getByTestId('auth-logout').click();
    await expect(page.getByTestId('auth-login')).toBeVisible();
    release();
    await expect(page.getByTestId('auth-login')).toBeVisible();
    // Flush pending UI work without exposing any content from another session.
    await page.waitForTimeout(350);
    expect(downloads).toEqual([]);
    await expect(page.getByTestId('notice')).not.toContainText('Esportazione JSON avviata');
  } finally {
    release();
  }
});


test('real OIDC private API responses are never HTTP cacheable', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral Keycloak E2E account');

  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  const privateRead = page.waitForResponse(response =>
    response.url().includes('/api/v1/memories/page?') && response.status() === 200);
  await page.getByTestId('sync-reload').click();
  const actual = await privateRead;
  // RED #173: real ASP.NET + JWT + PostgreSQL response currently omits
  // Cache-Control on successful private reads, unlike the export endpoint.
  expect(actual.headers()['cache-control']).toBe('no-store');
  expect(actual.headers()['x-content-type-options']).toBe('nosniff');
  expect(actual.headers()['referrer-policy']).toBe('no-referrer');

  // The server must apply the SAME policy to genuine bearer challenges.
  const challenge = await page.request.get('http://127.0.0.1:8080/api/v1/consents');
  expect(challenge.status()).toBe(401);
  expect(challenge.headers()['cache-control']).toBe('no-store');
  expect(challenge.headers()['x-content-type-options']).toBe('nosniff');
});


test('real owner can load older encrypted PostgreSQL notes beyond the first timeline page', async ({ page }) => {
  test.setTimeout(150_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC test account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  if (await page.getByTestId('consent-accept').count() > 0) {
    await page.getByTestId('consent-accept').click();
    await page.getByTestId('grant-consent').click();
  }
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  // Genuine UI -> Keycloak/JWT -> .NET API -> real PostgreSQL with RLS and
  // sealed payload. No fake response or bypass of consent.
  const prefix = 'Cronologia paginata E2E ' + Date.now();
  const first = prefix + ' numero 00';
  for (let n = 0; n < 12; n++) {
    const note = prefix + ' numero ' + String(n).padStart(2, '0');
    const stored = page.waitForResponse(r =>
      r.url().endsWith('/api/v1/memories') &&
      r.request().method() === 'POST' && r.status() === 200);
    await page.getByTestId('memo-input').fill(note);
    await page.getByTestId('save-memo').click();
    await stored;
    await expect(page.getByTestId('notice')).toContainText('Nota salvata');
    await expect(page.getByTestId('memo-input')).toHaveValue('');
  }

  // RED: the previous UI only fetched 30 and offered no way to browse older
  // rows. New UX initially shows 10 and must expose a real next-page cursor.
  await expect(page.getByTestId('load-older')).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(first)).toHaveCount(0);
  const pageTwo = page.waitForResponse(r =>
    r.url().includes('/api/v1/memories/page?') &&
    r.url().includes('before=') &&
    r.status() === 200);
  await page.getByTestId('load-older').click();
  const response = await pageTwo;
  expect(response.headers()['cache-control']).toBe('no-store');
  await expect(page.getByTestId('memory-timeline').getByText(first)).toBeVisible();
  expect(await page.getByTestId('memory-timeline').getByText(first).count()).toBe(1);

  await page.getByTestId('auth-logout').click();
  await expect(page.getByTestId('auth-login')).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(first)).toHaveCount(0);
  await expect(page.getByTestId('load-older')).toHaveCount(0);

  // After an actual new OIDC login the first page comes from the same
  // canonical PostgreSQL database, not an offline/session cache.
  await browserLogin(page, user1);
  await expect(page.getByTestId('memory-timeline').getByText(
    prefix + ' numero 11')).toBeVisible();
});


test('real signed capture idempotency rejects a different body with the same external key', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing synthetic Keycloak browser credentials');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  if (await page.getByTestId('consent-accept').count() > 0) {
    await page.getByTestId('consent-accept').click();
    await page.getByTestId('grant-consent').click();
  }
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  const originalNote = 'Nota canonica idempotente ' + Date.now();
  let repeatedStatus = -1;
  let conflictingStatus = -1;
  let firstStatus = -1;
  await page.route('**/api/v1/memories', async route => {
    if (route.request().method() !== 'POST') return route.continue();
    const originalPost = route.request().postDataJSON() as {
      source: string; externalId: string; contentType: string;
      content: string; occurredAt: string;
    };
    // Forward the original request with its actual OIDC bearer, exactly as
    // captured by the user. The real API executes its PostgreSQL transaction.
    const accepted = await route.fetch();
    firstStatus = accepted.status();
    // An identical network retry must be a no-op and retain the same UUID.
    const repeated = await route.fetch();
    repeatedStatus = repeated.status();
    // Same tenant/owner/source/externalId, but different private content:
    // this must never silently mutate an already accepted canonical note.
    const collision = await route.fetch({
      postData: JSON.stringify({ ...originalPost, content: originalPost.content + ' ALTERATO' })
    });
    conflictingStatus = collision.status();
    await route.fulfill({ response: accepted });
  });

  await page.getByTestId('memo-input').fill(originalNote);
  await page.getByTestId('save-memo').click();
  await expect(page.getByTestId('notice')).toContainText('Nota salvata');
  expect(firstStatus).toBe(200);
  expect(repeatedStatus).toBe(200);
  // RED: actual PostgreSQL ON CONFLICT DO UPDATE returned 200 and replaced
  // original text. GREEN: generic 409 with no private content disclosure.
  expect(conflictingStatus).toBe(409);

  await page.getByTestId('sync-reload').click();
  await expect(page.getByTestId('memory-timeline').getByText(originalNote)).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(originalNote + ' ALTERATO'))
    .toHaveCount(0);
});


test('real Keycloak stale-device withdrawal revokes the newer same-scope consent', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral real OIDC account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);

  // Independent of preceding E2E cases: start from an explicitly revoked
  // manual source, then grant G1 via the browser under actual signed JWT.
  if (await page.getByTestId('revoke-consent').count() > 0) {
    await page.getByTestId('revoke-consent').click();
    await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  }
  await page.getByTestId('consent-accept').click();
  await page.getByTestId('grant-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  let regrantStatus = -1;
  let originalDeleteStatus = -1;
  let signedBearer = '';
  await page.route('**/api/v1/consents/*', async route => {
    if (route.request().method() !== 'DELETE') return route.continue();
    signedBearer = route.request().headers()['authorization'] ?? '';
    expect(signedBearer).toMatch(/^Bearer .{20,}$/);

    // Orchestrate the genuine two-device race: before G1's DELETE reaches
    // ASP.NET, another authorized action creates G2. No fake token, response
    // or DB mutation: actual Keycloak bearer and .NET consent POST.
    const g2 = await page.request.post('http://127.0.0.1:8080/api/v1/consents', {
      headers: { Authorization: signedBearer },
      data: {
        source: 'manual', purpose: 'memory.capture',
        policyVersion: 'privacy-v1',
        expiresAt: new Date(Date.now() + 20 * 24 * 3600 * 1000).toISOString(),
        accepted: true
      }
    });
    regrantStatus = g2.status();
    const actualDelete = await route.fetch();
    originalDeleteStatus = actualDelete.status();
    await route.fulfill({ response: actualDelete });
  });

  await page.getByTestId('revoke-consent').click();
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');
  expect(regrantStatus).toBe(201);
  expect(originalDeleteStatus).toBe(204);

  const read = page.waitForResponse(r =>
    r.url().endsWith('/api/v1/consents') &&
    r.request().method() === 'GET' && r.status() === 200);
  await page.getByTestId('sync-reload').click();
  const actual = await read;
  const ledger = await actual.json() as Array<{
    source: string; purpose: string; active: boolean;
  }>;
  // RED: old DELETE(G1) succeeded but G2 remained active in real PostgreSQL
  // and UI silently re-displays "Autorizzazione attiva" after refresh.
  expect(ledger.some(g =>
    g.source === 'manual' && g.purpose === 'memory.capture' && g.active)).toBe(false);
  await expect(page.getByTestId('consent-state')).toContainText('Nessun consenso attivo');

  const forbidden = await page.request.post('http://127.0.0.1:8080/api/v1/memories', {
    headers: { Authorization: signedBearer },
    data: {
      source: 'manual', externalId: 'after-stale-withdrawal-' + Date.now(),
      contentType: 'text/plain', content: 'synthetic consent withdrawal must deny',
      occurredAt: new Date().toISOString()
    }
  });
  expect(forbidden.status()).toBe(403);
  expect((await forbidden.json()).error).toBe('consent_required');
});


test('real Keycloak captures at the same clock instant must not collide (#177)', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral real OIDC account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);
  if (await page.getByTestId('consent-accept').count() > 0) {
    await page.getByTestId('consent-accept').click();
    await page.getByTestId('grant-consent').click();
  }
  await expect(page.getByTestId('consent-state')).toContainText('Autorizzazione attiva');

  // Freeze only the capture ISO timestamp, not the Keycloak token clock:
  // actual browser -> bearer JWT -> ASP.NET -> PostgreSQL remain untouched.
  const fixedInstant = await page.evaluate(() => {
    const instant = new Date().toISOString();
    Date.prototype.toISOString = () => instant;
    return instant;
  });
  const sent: Array<{ occurredAt: string; externalId: string }> = [];
  page.on('request', req => {
    if (req.method() === 'POST' && req.url().endsWith('/api/v1/memories')) {
      const body = req.postDataJSON() as { occurredAt: string; externalId: string };
      sent.push({ occurredAt: body.occurredAt, externalId: body.externalId });
    }
  });
  const firstNote = 'Prima nota stesso istante ' + Date.now();
  const secondNote = 'Seconda nota stesso istante ' + Date.now();
  await page.getByTestId('memo-input').fill(firstNote);
  const firstPost = page.waitForResponse(r =>
    r.url().endsWith('/api/v1/memories') && r.request().method() === 'POST');
  await page.getByTestId('save-memo').click();
  expect((await firstPost).status()).toBe(200);
  await expect(page.getByTestId('memory-timeline').getByText(firstNote)).toBeVisible();

  await page.getByTestId('memo-input').fill(secondNote);
  const secondPost = page.waitForResponse(r =>
    r.url().endsWith('/api/v1/memories') && r.request().method() === 'POST');
  await page.getByTestId('save-memo').click();
  // RED: old Date-derived externalId causes a real PostgreSQL HTTP 409.
  expect((await secondPost).status()).toBe(200);
  await expect(page.getByTestId('memory-timeline').getByText(secondNote)).toBeVisible();
  await expect(page.getByTestId('memory-timeline').getByText(firstNote)).toBeVisible();
  expect(sent).toHaveLength(2);
  expect(sent.map(s => s.occurredAt)).toEqual([fixedInstant, fixedInstant]);
  expect(sent[0].externalId).not.toBe(sent[1].externalId);
});


test('real Keycloak JWT can read bounded consent history and rejects invalid cursors (#180)', async ({ page }) => {
  test.setTimeout(120_000);
  if (!user1 || !password) throw new Error('Missing ephemeral OIDC account');
  await page.goto('/Omnia/');
  await browserLogin(page, user1);

  // Observe the real browser's authenticated request without mocking the
  // provider, token exchange, ASP.NET route or PostgreSQL response.
  // The bearer is never printed, persisted or exposed to the UI.
  let bearer = '';
  await page.route('**/api/v1/consents', async route => {
    if (route.request().method() === 'GET')
      bearer = route.request().headers()['authorization'] ?? '';
    await route.continue();
  });
  const refreshed = page.waitForResponse(response =>
    response.url().endsWith('/api/v1/consents') &&
    response.request().method() === 'GET' && response.status() === 200);
  await page.getByTestId('sync-reload').click();
  await refreshed;
  expect(bearer).toMatch(/^Bearer .{20,}$/);

  const endpoint = 'http://127.0.0.1:8080/api/v1/consents/page';
  const first = await page.request.get(endpoint + '?limit=2', {
    headers: { Authorization: bearer }
  });
  expect(first.status()).toBe(200);
  expect(first.headers()['cache-control']).toContain('no-store');
  const history = await first.json() as {
    items: Array<{ id: string; source: string; purpose: string; active: boolean }>;
    nextCursor: string | null;
  };
  expect(history.items.length).toBeLessThanOrEqual(2);
  for (const item of history.items) {
    expect(item.id).toMatch(/^[0-9a-f-]{36}$/i);
    expect(item.purpose).toBe('memory.capture');
  }
  if (history.nextCursor) {
    const second = await page.request.get(endpoint +
      '?limit=2&before=' + encodeURIComponent(history.nextCursor), {
      headers: { Authorization: bearer }
    });
    expect(second.status()).toBe(200);
    const newerIds = new Set(history.items.map(item => item.id));
    const next = await second.json() as { items: Array<{ id: string }> };
    for (const item of next.items) expect(newerIds.has(item.id)).toBe(false);
  }
  const invalid = await page.request.get(endpoint + '?before=invalid', {
    headers: { Authorization: bearer }
  });
  expect(invalid.status()).toBe(400);
  expect((await invalid.json()).error).toBe('invalid_consent_page');
  expect((await page.request.get(endpoint)).status()).toBe(401);
});
