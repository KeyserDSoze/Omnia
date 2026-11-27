import { test, expect } from '@playwright/test';
import { fakeBrowserSpeech } from './fake-speech';

// Runs only against the on-prem docker compose stack (nginx :59800 -> web, /api, /auth).
test.skip(process.env.TAO_GATEWAY_E2E !== '1', 'Run via the gateway CI job.');
const username = process.env.TAO_E2E_USER1 ?? '';
const password = process.env.TAO_E2E_PASSWORD ?? '';

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => window.localStorage.setItem('tao.push.invite', 'never'));
});

test('single port: chat-first setup saved in PostgreSQL, settings, voice consent, deck and a note through /api', async ({ page }) => {
  test.setTimeout(120_000);
  if (!username || !password) throw new Error('Missing disposable gateway account');
  await fakeBrowserSpeech(page);

  await page.goto('/');
  // Signed out: Tao only says hello and asks to sign in; the tiny header offers the same.
  await expect(page.getByTestId('chat-tao').first()).toHaveText('Ciao.');
  await expect(page.getByTestId('header-login')).toBeVisible();
  await expect(page.getByTestId('chat-input')).toHaveCount(0);
  await page.getByTestId('chat-login').click();
  // Same-origin OIDC: the authorization request never leaves the gateway origin.
  await expect(page).toHaveURL(/^http:\/\/localhost:59800\/auth\/realms\/tao\//);
  await page.locator('input[name="username"]').fill(username);
  await page.locator('input[name="password"]').fill(password);
  await page.locator('#kc-login').click();

  // A new user lands on the chat with the magic button; the setup is scripted (no AI).
  await expect(page.getByTestId('setup-start')).toBeVisible({ timeout: 45_000 });
  await expect(page.getByTestId('chat-tao').nth(1)).toContainText('configuriamo insieme');
  await page.getByTestId('setup-start').click();
  await page.getByTestId('chat-input').fill('Gateway');
  await page.getByTestId('chat-send').click();
  await page.getByTestId('setup-choice-work').click();
  await page.getByTestId('setup-choice-outlook').click();
  await page.getByTestId('setup-choice-teams').click();
  await page.getByTestId('setup-confirm').click();
  await page.getByTestId('setup-choice-no').click();
  await page.getByTestId('setup-choice-no').click();
  await page.getByTestId('setup-choice-none').click();
  await page.getByTestId('setup-choice-08:00').click();
  await page.getByTestId('setup-choice-17:00').click();
  await page.getByTestId('setup-choice-read_only').click();
  await page.getByTestId('setup-choice-save').click();
  await expect(page.getByTestId('chat-tao').filter({ hasText: 'Configurazione salvata' })).toBeVisible({ timeout: 15_000 });
  // Connectors are not configured in this stack: Tao says so instead of opening a broken popup.
  await expect(page.getByTestId('chat-tao').last()).toContainText('docs/connectors.md');

  // Dictation through the AI router: the browser-speech consent is stored by the API first.
  await page.getByTestId('chat-mic').click();
  await expect(page.getByTestId('chat-tao').last()).toContainText('servizi vocali del tuo browser', { timeout: 15_000 });
  await page.getByTestId('voice-consent-accept').click();
  await expect(page.getByTestId('chat-tao').filter({ hasText: 'Servizi vocali attivi per 30 giorni' })).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId('chat-user').last()).toHaveText('cosa devo fare oggi');
  await expect(page.getByTestId('today-empty-accounts')).toBeVisible({ timeout: 15_000 });

  // Settings (top right) show the account and what the agent may do per account, from PostgreSQL.
  await page.getByTestId('open-settings').click();
  await expect(page.getByTestId('settings-panel')).toBeVisible();
  await expect(page.getByTestId('settings-speech')).toContainText('attivi fino al', { timeout: 15_000 });
  await page.getByTestId('settings-speech-revoke').click();
  await expect(page.getByTestId('settings-message')).toContainText('revocati', { timeout: 15_000 });
  await expect(page.getByTestId('settings-account')).toContainText('Microsoft 365 di lavoro');
  await expect(page.getByTestId('delegation-chats.read')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByTestId('delegation-tasks.read')).toHaveAttribute('aria-pressed', 'false');
  await page.getByTestId('delegation-tasks.read').click();
  await expect(page.getByTestId('settings-message')).toContainText('Attività attivato', { timeout: 15_000 });
  await expect(page.getByTestId('connections-panel')).toContainText('Google Workspace');
  await page.reload();
  await page.getByTestId('chat-login').click();
  await expect(page.getByTestId('chat-tao').first()).toContainText('Gateway', { timeout: 45_000 });
  await page.getByTestId('open-settings').click();
  await expect(page.getByTestId('delegation-tasks.read')).toHaveAttribute('aria-pressed', 'true');

  // The chat drives the deck and the (unconfigured) assistant without any AI.
  await page.getByTestId('open-chat').click();
  await page.getByTestId('chat-hint-today').click();
  await expect(page.getByTestId('today-empty-accounts')).toBeVisible({ timeout: 15_000 });
  await page.getByTestId('chat-input').fill('fammi il piano');
  await page.getByTestId('chat-send').click();
  await expect(page.getByTestId('briefing-unconfigured')).toBeVisible();

  await page.getByTestId('open-settings').click();
  await page.getByTestId('settings-open-memory').click();
  await expect(page.getByTestId('auth-status')).toContainText('Sessione autenticata');
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
