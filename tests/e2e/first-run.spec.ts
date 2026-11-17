import { test, expect } from '@playwright/test';

test('la pagina web Omnia mostra cattura, privacy e roadmap', async ({ page }) => {
  await page.goto('/Omnia');
  await expect(page.getByText('La tua giornata.', { exact: false })).toBeVisible();
  await expect(page.getByTestId('memo-input')).toBeVisible();
  await expect(page.getByText('NESSUNA AUTOMAZIONE ATTIVA', { exact: false })).toBeVisible();
});

test('tema e nota rapida funzionano senza simulare sincronizzazione', async ({ page }) => {
  await page.goto('/Omnia');
  const toggle = page.getByTestId('theme-toggle');
  await toggle.click();
  await page.getByTestId('memo-input').fill('Riunione con il cliente');
  await page.getByTestId('save-memo').click();
  await expect(page.getByText('Riunione con il cliente', { exact: true })).toBeVisible();
  await expect(page.getByTestId('notice')).toContainText('questa sessione');
});
