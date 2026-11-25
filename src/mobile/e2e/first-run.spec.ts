import { test, expect } from '@playwright/test';

const appPath = `${(process.env.TAO_WEB_BASE_PATH ?? '').replace(/\/+$/, '')}/`;

test('la pagina web Tao mostra cattura, privacy e roadmap', async ({ page }) => {
  await page.goto(appPath);
  await expect(page.getByText('La tua giornata.', { exact: false })).toBeVisible();
  await expect(page.getByTestId('memo-input')).toBeVisible();
  await expect(page.getByText('NESSUNA AUTOMAZIONE ATTIVA', { exact: false })).toBeVisible();
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
