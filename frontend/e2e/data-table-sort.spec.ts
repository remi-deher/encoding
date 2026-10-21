import { expect, test } from '@playwright/test';

const profiles = [0, 1, 2].map((index) => ({
  id: `profile-${index}`,
  draftRevision: 1,
  draftName: `Profil ${index}`,
  draftDescription: 'Description',
  publishedVersion: 1,
  encoderCount: 2,
  updatedAt: '2026-10-20T10:00:00Z',
  isBuiltIn: false,
  versions: [],
}));

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  await page.route('**/api/v1/encoding-profiles', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(profiles) }),
  );
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        setupRequired: false,
        user: { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' },
      }),
    }),
  );
});

test('the card sort control stays compact and the page does not scroll sideways', async ({
  page,
}) => {
  await page.goto('/libraries/encoding-profiles');
  const sort = page.locator('.card-sort mat-form-field');
  await expect(sort).toBeVisible();
  const box = await sort.boundingBox();
  const viewport = page.viewportSize();
  expect(box!.width).toBeLessThan(viewport!.width);
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});
