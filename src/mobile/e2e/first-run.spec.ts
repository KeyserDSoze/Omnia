import { test, expect } from '@playwright/test';

const appPath = `${(process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '')}/`;

test('la pagina web Tao mostra cattura, privacy e roadmap', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  // A dark device differs from the (light) static render: the classic hydration trap.
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto(appPath);
  await expect(page.getByText('La tua giornata.', { exact: false })).toBeVisible();
  await expect(page.getByTestId('memo-input')).toBeVisible();
  await expect(page.getByText('NESSUNA AZIONE SENZA LA TUA CONFERMA', { exact: false })).toBeVisible();
  // Static export and first client render must agree (no React hydration error #418).
  expect(errors.filter(e => /418|hydrat/i.test(e))).toEqual([]);
});

test('il callback dei connettori consegna state e code alla finestra Tao e li toglie dall’URL', async ({ context }) => {
  const app = await context.newPage();
  await app.goto(appPath);
  const received = app.evaluate(() => new Promise(resolve => {
    const channel = new BroadcastChannel('tao.connectors');
    channel.onmessage = event => { channel.close(); resolve(event.data); };
  }));
  const state = 'Q2hhbmdlLW1lLXN0YXRlLXZhbHVlLTAxMjM0NTY3ODk';
  const popup = await context.newPage();
  await popup.goto(`${appPath}connections/callback?code=one-time-code&state=${state}`);
  await expect(popup.getByTestId('connector-callback')).toContainText('torna alla finestra di Tao');
  expect(new URL(popup.url()).search).toBe('');
  expect(await received).toEqual({ type: 'tao.connector.callback', state, code: 'one-time-code', error: null });
});

test('tema e nota rapida funzionano senza simulare sincronizzazione', async ({ page }) => {
  await page.goto(appPath);
  const toggle = page.getByTestId('theme-toggle');
  await toggle.click();
  await page.getByTestId('memo-input').fill('Riunione con il cliente');
  await page.getByTestId('save-memo').click();
  await expect(page.getByText('Riunione con il cliente', { exact: true })).toBeVisible();
  await expect(page.getByTestId('notice')).toContainText('questa sessione');
});

test('demo: il deck di oggi si smista con fatto, dopo, scarta e completamento esplicito', async ({ page }) => {
  // Only the static demo build shows synthetic cards; the on-prem build needs a signed session.
  test.skip(!!process.env.TAO_E2E_BASE_URL, 'Demo deck exists only in the static export.');
  await page.goto(appPath);
  const title = page.getByTestId('today-card-title');
  await expect(title).toHaveText('Rinnovare il dominio aziendale');
  await page.getByTestId('today-apply-source').click();
  await expect(page.getByTestId('today-apply-source')).toHaveAttribute('aria-checked', 'true');
  await page.getByTestId('today-done').click();
  await expect(title).toHaveText('Inviare l’offerta a ACME');
  // The opt-in is per card: the next task starts unchecked.
  await expect(page.getByTestId('today-apply-source')).toHaveAttribute('aria-checked', 'false');
  await page.getByTestId('today-later').click();
  await expect(title).toHaveText('Standup di team');
  const swipe = async (dx: number) => {
    const box = (await page.getByTestId('today-card').boundingBox())!;
    const y = box.y + 40;
    await page.mouse.move(box.x + box.width / 2, y);
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 + dx, y, { steps: 12 });
    await page.mouse.up();
  };
  await swipe(-220); // left = not done
  await expect(title).toHaveText('Contratto da firmare entro venerdì');
  await page.getByTestId('today-dismiss').click();
  await expect(title).toHaveText('Luca Neri');
  await swipe(220); // right = done
  await expect(page.getByTestId('today-clear')).toBeVisible();
  await expect(page.getByTestId('today-summary')).toHaveText('Fatte 2 · Non fatte 1 · Rimandate 1 · Scartate 1');
});
