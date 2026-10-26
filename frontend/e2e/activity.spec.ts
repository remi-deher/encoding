import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

const now = '2026-10-23T12:00:00Z';
const running = {
  id: 'run-1',
  media: 'Séries/Épisode en cours.mkv',
  library: 'Séries',
  flow: 'HEVC',
  flowVersion: 3,
  state: 'running',
  nodeId: 'node-1',
  node: 'Studio',
  progress: 100,
  detail: 'Validation avant publication',
  attempt: 1,
  revision: 3,
  queuedAt: now,
  startedAt: now,
  finishedAt: null,
  updatedAt: now,
};
const counts = {
  activeCount: 2,
  queuedCount: 6000,
  succeededCount: 60,
  failedCount: 1,
  cancelledCount: 0,
  deferredCount: 0,
  activity: [],
  observedAt: now,
};

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'admin', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/cluster', (route) =>
    route.fulfill({ json: { id: 'cluster', name: 'Maison', standalone: true } }),
  );
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/executions*', (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.endsWith('/stream')) return route.abort();
    const state = url.searchParams.get('state');
    const offset = Number(url.searchParams.get('offset') ?? 0);
    const limit = Number(url.searchParams.get('limit') ?? 25);
    let executions: (typeof running)[] = [];
    let totalCount = 0;
    if (state === 'succeeded') {
      totalCount = 60;
      executions = Array.from({ length: Math.min(limit, Math.max(0, 60 - offset)) }, (_, i) => ({
        ...running,
        id: 'done-' + (offset + i),
        media: `Film terminé ${offset + i}.mkv`,
        state: 'succeeded',
        finishedAt: now,
      }));
    } else if (state === 'running') {
      executions = [running];
      totalCount = 1;
    } else if (state === 'cancelling') {
      executions = [
        {
          ...running,
          id: 'cancel-1',
          media: 'Annulation.mkv',
          state: 'cancelling',
          progress: null,
        },
      ];
      totalCount = 1;
    } else if (!state || state === 'queued') {
      executions = [
        { ...running, id: 'queue-1', media: 'En attente.mkv', state: 'queued', progress: null },
      ];
      totalCount = 6000;
    }
    return route.fulfill({ json: { ...counts, executions, totalCount, limit, offset } });
  });
});

test('completed jobs stay visible behind a large queue and history is paginated', async ({
  page,
}) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  await activity.getByRole('option', { name: /Terminés/ }).click();
  await activity.getByRole('button', { name: 'Voir la suite : terminés' }).click();
  await expect(activity.getByText('Film terminé 25.mkv', { exact: true })).toBeVisible();
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toHaveCount(0);
  await activity.getByRole('button', { name: 'Page précédente' }).click();
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  expect(errors).toEqual([]);
});

test('jobs pill opens an accessible anchored panel with actual running and cancelling jobs', async ({
  page,
}) => {
  await page.goto('/activity');
  const trigger = page.getByRole('button', { name: '2 jobs en cours', exact: true });
  await trigger.click();
  const panel = page.getByRole('dialog', { name: 'Jobs en cours', exact: true });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Épisode en cours.mkv');
  await expect(panel).toContainText('Validation avant publication');
  await expect(panel).toContainText('Annulation demandée');
  await expect(panel).toContainText('En cours');
  await expect(panel.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '100');
  const accessibility = await new AxeBuilder({ page })
    .include('[role="dialog"][aria-label="Jobs en cours"]')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(accessibility.violations.map((violation) => violation.id)).toEqual([]);
  await page.keyboard.press('Escape');
  await expect(panel).not.toBeVisible();
  await expect(trigger).toBeFocused();
  await trigger.click();
  await panel.getByRole('link', { name: 'Voir l’activité' }).click();
  await expect(panel).not.toBeVisible();
  await expect(page).toHaveURL(/view=active/);
});

test('viewers do not request administrator-only AI history', async ({ page }) => {
  let aiRequests = 0;
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: 'viewer', username: 'viewer', role: 'viewer' } },
    }),
  );
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) => {
    aiRequests++;
    return route.fulfill({ status: 403, json: {} });
  });
  await page.goto('/activity?view=history');
  await expect(page.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  expect(aiRequests).toBe(0);
  await expect(
    page.getByText('Les demandes de l’assistant sont momentanément indisponibles.'),
  ).toHaveCount(0);
});

test('AI history survives a temporary outage and recovers on refresh', async ({ page }) => {
  let unavailable = false;
  await page.route('**/api/v1/tools/ai/assistant/activity', (route) =>
    unavailable
      ? route.fulfill({ status: 503, json: {} })
      : route.fulfill({
          json: [
            {
              requestId: 'ai-1',
              conversationTitle: 'Analyse du flow',
              status: 'succeeded',
              provider: 'ollama',
              startedAt: now,
              updatedAt: now,
              durationMilliseconds: 500,
              errorCategory: null,
            },
          ],
        }),
  );
  await page.goto('/activity?type=ai');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Analyse du flow', { exact: true })).toBeVisible();
  unavailable = true;
  await activity.getByRole('button', { name: 'Actualiser', exact: true }).click();
  await expect(activity.getByRole('status')).toContainText('Les demandes de l’assistant');
  await expect(activity.getByText('Analyse du flow', { exact: true })).toBeVisible();
  unavailable = false;
  await activity.getByRole('button', { name: 'Actualiser', exact: true }).click();
  await expect(
    activity.getByText('Les demandes de l’assistant sont momentanément indisponibles.'),
  ).toHaveCount(0);
});

test('a failed refresh preserves history and a retry recovers, with responsive light and dark themes', async ({
  page,
}, info) => {
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  let unavailable = true;
  await page.route('**/api/v1/executions?state=succeeded*', (route) =>
    unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback(),
  );
  await activity.getByRole('button', { name: 'Actualiser', exact: true }).click();
  await expect(activity.getByRole('status')).toContainText('Actualisation impossible');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = false;
  await activity.getByRole('button', { name: 'Actualiser', exact: true }).click();
  await expect(activity.getByText('Actualisation impossible', { exact: false })).toHaveCount(0);
  for (const theme of ['light', 'dark']) {
    await page.evaluate((value) => (document.documentElement.dataset['theme'] = value), theme);
    await page.evaluate(async () => {
      await Promise.all(
        document.getAnimations().map((animation) => animation.finished.catch(() => undefined)),
      );
    });
    const result = await new AxeBuilder({ page })
      .include('app-activity-page')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(
      result.violations.map((v) => ({ id: v.id, targets: v.nodes.map((n) => n.target) })),
    ).toEqual([]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(
      true,
    );
    await page.screenshot({ path: info.outputPath(`activity-${theme}.png`), fullPage: true });
  }
});
