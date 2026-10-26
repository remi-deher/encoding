import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: 'admin', username: 'admin', role: 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/tools', (route) =>
    route.fulfill({
      json: [
        {
          id: 'ffmpeg',
          name: 'FFmpeg',
          role: 'Encodage vidéo et audio',
          provider: 'stock',
          stockVersion: '8',
          conformanceStatus: 'ready',
          probePreserved: true,
          executables: ['ffmpeg'],
          mediaTypes: ['video', 'audio'],
          encoders: [],
          clusterReadiness: 'Disponible',
        },
      ],
    }),
  );
});

test('palette menu changes Material and application colours, preserves mode and survives reload', async ({
  page,
}) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Choisir la palette de couleurs' }).click();
  await page.getByRole('menuitemradio', { name: 'Iris' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-palette', 'iris');
  await page.getByRole('button', { name: 'Passer au thème sombre' }).click();
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-palette', 'iris');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.getByRole('button', { name: 'Choisir la palette de couleurs' }).click();
  await expect(page.getByRole('menuitemradio', { name: 'Iris' })).toHaveAttribute(
    'aria-checked',
    'true',
  );
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'Choisir la palette de couleurs' })).toBeFocused();
});

test('populated tools stay readable in four palettes and both modes without mobile overflow', async ({
  page,
}, info) => {
  await page.goto('/plugins');
  await page.getByRole('tab', { name: /Outils média/ }).click();
  await expect(page.getByRole('heading', { name: 'FFmpeg', exact: true })).toBeVisible();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const colours: string[] = [];
  for (const palette of ['forest', 'ocean', 'iris', 'rose']) {
    await page.getByRole('button', { name: 'Choisir la palette de couleurs' }).click();
    await page
      .getByRole('menuitemradio', {
        name: { forest: 'Forêt', ocean: 'Océan', iris: 'Iris', rose: 'Rose' }[palette],
        exact: true,
      })
      .click();
    colours.push(
      await page.evaluate(() =>
        getComputedStyle(document.documentElement).getPropertyValue('--mat-sys-primary'),
      ),
    );
    for (const mode of ['light', 'dark']) {
      await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), mode);
      await page.evaluate(async () => {
        await Promise.all(
          document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
        );
      });
      const result = await new AxeBuilder({ page })
        .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
        .analyze();
      expect(
        result.violations.map((item) => ({
          id: item.id,
          targets: item.nodes.map((node) => node.target),
        })),
      ).toEqual([]);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
        true,
      );
      await page.screenshot({
        path: info.outputPath(`tools-${palette}-${mode}.png`),
        fullPage: true,
      });
    }
  }
  expect(new Set(colours).size).toBe(4);
  await page.setViewportSize({ width: 320, height: 800 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await expect(page.getByRole('heading', { name: 'FFmpeg', exact: true })).toBeVisible();
});

test('adaptive shell keeps all destinations reachable at tablet width and supports pointer search', async ({
  page,
}) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await page.goto('/');
  await expect(page.locator('.application-sidebar')).toHaveClass(/is-collapsed/);
  await expect(page.getByRole('link', { name: 'Paramètres', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Rechercher une destination', exact: true }).click();
  const search = page.getByRole('combobox', { name: /Rechercher une destination/ });
  await search.fill('Profils');
  await search.press('Enter');
  await expect(page).toHaveURL(/libraries\/encoding-profiles/);
});

test('store cards and repository actions stay accessible with populated content on narrow screens', async ({
  page,
}) => {
  await page.route('**/api/v1/store/repositories', (route) =>
    route.fulfill({
      json: [
        {
          id: 'repo',
          name: 'Dépôt de test',
          url: 'https://example.org/plugins/repository.json',
          description: '',
          isOfficial: false,
          isLocked: false,
          enabled: true,
          packagesCount: 1,
        },
      ],
    }),
  );
  await page.route('**/api/v1/store/packages', (route) =>
    route.fulfill({
      json: [
        {
          id: 'pkg',
          name: 'Outil de test',
          kind: 'tool',
          category: 'tools',
          categoryLabel: 'Outil',
          version: '1.0',
          installedVersion: null,
          hasUpdate: false,
          author: 'Tests',
          description: 'Un outil de démonstration pour vérifier le rendu du Store.',
          requiredTools: [],
          repositoryId: 'repo',
          icon: '◇',
        },
      ],
    }),
  );
  await page.goto('/plugins');
  await page.getByRole('tab', { name: /Store & Dépôts/ }).click();
  await expect(page.getByRole('heading', { name: 'Outil de test' })).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Importer une archive (.zip)', exact: true }),
  ).toBeVisible();
  await page.emulateMedia({ reducedMotion: 'reduce' });
  for (const mode of ['light', 'dark']) {
    await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), mode);
    await page.evaluate(async () => {
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
      );
    });
    const audit = await new AxeBuilder({ page })
      .include('app-plugin-catalog-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(audit.violations.map((item) => item.id)).toEqual([]);
  }
  await page.setViewportSize({ width: 320, height: 800 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
