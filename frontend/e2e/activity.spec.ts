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
const activityCounts = { untreated: 6000, active: 2, done: 60, failed: 1, disabled: 0 };

function activityResponse(status: string, offset: number, limit: number) {
  const totalCount = activityCounts[status as keyof typeof activityCounts] ?? 0;
  const files =
    status === 'done'
      ? Array.from({ length: Math.min(limit, Math.max(0, totalCount - offset)) }, (_, index) => ({
          id: `file-${offset + index}`,
          libraryId: 'library-1',
          library: 'Films',
          relativePath: `Film terminé ${offset + index}.mkv`,
          bytes: 1024,
          modifiedAt: now,
          libraryActive: true,
          flow: 'HEVC',
          flowVersion: 3,
          status: 'done',
          executionId: `done-${offset + index}`,
          executionState: 'succeeded',
          detail: 'Traitement terminé',
          nodeId: 'node-1',
          node: 'Studio',
          progress: 100,
          updatedAt: now,
        }))
      : [];
  return {
    files,
    counts: activityCounts,
    totalCount,
    limit,
    offset,
    observedAt: now,
  };
}

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
  await page.route('**/api/v1/activity/files**', (route) => {
    const url = new URL(route.request().url());
    return route.fulfill({
      json: activityResponse(
        url.searchParams.get('status') ?? 'untreated',
        Number(url.searchParams.get('offset') ?? 0),
        Number(url.searchParams.get('limit') ?? 50),
      ),
    });
  });
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
  await expect(activity.getByRole('tab', { name: /Déjà traité/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
  await expect(activity).toContainText('6063 fichiers suivis');
  await activity.getByRole('button', { name: 'Afficher plus de fichiers' }).click();
  await expect(activity.getByText('Film terminé 50.mkv', { exact: true })).toBeVisible();
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

test('activity keeps the last results visible through a refresh outage and recovers automatically', async ({
  page,
}) => {
  let unavailable = false;
  await page.clock.install();
  await page.route('**/api/v1/activity/files**', (route) =>
    unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback(),
  );
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = true;
  await page.clock.fastForward(10_000);
  await expect(activity.getByRole('status')).toContainText('L’activité n’a pas pu être actualisée');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = false;
  await page.clock.fastForward(10_000);
  await expect(
    activity.getByText('L’activité n’a pas pu être actualisée.', { exact: false }),
  ).toHaveCount(0);
});

test('background failures preserve history and recover automatically, with responsive light and dark themes', async ({
  page,
}, info) => {
  let unavailable = false;
  await page.route('**/api/v1/activity/files**', (route) =>
    unavailable ? route.fulfill({ status: 503, json: {} }) : route.fallback(),
  );
  await page.clock.install();
  await page.goto('/activity?view=history');
  const activity = page.locator('app-activity-page');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = true;
  await page.clock.fastForward(10_000);
  await expect(activity.getByRole('status')).toContainText('L’activité n’a pas pu être actualisée');
  await expect(activity.getByText('Film terminé 0.mkv', { exact: true })).toBeVisible();
  unavailable = false;
  await page.clock.fastForward(10_000);
  await expect(
    activity.getByText('L’activité n’a pas pu être actualisée.', { exact: false }),
  ).toHaveCount(0);
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
