import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'reader', username: 'reader', role: 'viewer' } },
    }),
  );
});

const status = {
  installedVersion: '1.9.0',
  latestVersion: '1.10.0',
  updateAvailable: true,
  status: 'available',
  checkedAt: '2026-10-24T12:00:00Z',
  releases: [
    {
      version: '1.10.0',
      publishedAt: '2026-10-24T12:00:00Z',
      notes: 'Correction <script>alert(1)</script>\nNouveaux outils.',
    },
    { version: '1.9.0', publishedAt: '2026-10-23T12:00:00Z', notes: 'Amélioration des flows.' },
  ],
};

test('shows versions, safely displays notes and works on desktop and mobile', async ({ page }) => {
  await page.route('**/api/v1/system/updates', (route) => route.fulfill({ json: status }));
  await page.goto('/updates');
  await expect(page.getByRole('heading', { name: 'Mise à jour', exact: true })).toBeVisible();
  await expect(page.getByText('Une nouvelle version est disponible : 1.10.0.')).toBeVisible();
  await expect(page.getByText('Installée sur ce nœud')).toBeVisible();
  await expect(
    page.getByText('Correction <script>alert(1)</script>', { exact: false }),
  ).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  expect((await new AxeBuilder({ page }).include('app-updates-page').analyze()).violations).toEqual(
    [],
  );
  await page.screenshot({
    path: `/tmp/encoding-updates-${test.info().project.name}.png`,
    fullPage: true,
  });
});

test('distinguishes a failed check from an up-to-date installation and allows retry', async ({
  page,
}) => {
  await page.route('**/api/v1/system/updates', (route) => route.fulfill({ status: 503 }));
  await page.goto('/updates');
  await expect(
    page.getByText('Impossible de charger les informations de version. Réessayez.'),
  ).toBeVisible();
  await expect(page.getByText('Ce nœud est à jour.')).toHaveCount(0);
  await page.route('**/api/v1/system/updates', (route) =>
    route.fulfill({
      json: { ...status, status: 'unavailable', latestVersion: null, updateAvailable: null },
    }),
  );
  await page.getByRole('button', { name: 'Vérifier les mises à jour' }).click();
  await expect(page.getByText('Vérification indisponible.', { exact: false })).toBeVisible();
  await expect(page.getByText('Amélioration des flows.')).toBeVisible();
});
