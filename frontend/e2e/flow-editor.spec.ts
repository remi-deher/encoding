import { expect, test, Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const output = (id: string) => ({
  id,
  label: id === 'success' ? 'Suite' : id === 'yes' ? 'Oui' : 'Non',
  description: 'Chemin ' + id,
});
const component = (id: string, kind: string, outputs: string[]) => ({
  id,
  version: '1.0.0',
  name: id,
  description: 'Description ' + id,
  kind,
  mediaType: 'general',
  outputs: outputs.map(output),
  configurationSchema: { version: 1, fields: [] },
});
const components = [
  component('base-input-file', 'input', ['success']),
  component('decision', 'condition', ['yes', 'no']),
  component('action', 'action', ['success']),
  component('finish', 'terminal', []),
];
const reference = (id: string) => ({
  pluginId: 'base',
  pluginVersion: '1.0.0',
  componentId: id,
  componentVersion: '1.0.0',
  configurationSchemaVersion: 1,
});
const node = (id: string, componentId: string) => ({
  id,
  name: id,
  component: reference(componentId),
  configuration: {},
});
const edge = (id: string, s: string, o: string, t: string) => ({
  id,
  sourceNodeId: s,
  sourceOutputId: o,
  targetNodeId: t,
});
const flow = {
  formatVersion: 1,
  id: '11111111-1111-4111-8111-111111111111',
  version: 1,
  name: 'Flow de test',
  mediaType: 'video',
  nodes: [
    node('Entrée', 'base-input-file'),
    node('Décision', 'decision'),
    node('Action A', 'action'),
    node('Action B', 'action'),
    node('Fin', 'finish'),
  ],
  connections: [
    edge('e1', 'Entrée', 'success', 'Décision'),
    edge('e2', 'Décision', 'yes', 'Action A'),
    edge('e3', 'Décision', 'no', 'Action B'),
    edge('e4', 'Action A', 'success', 'Fin'),
    edge('e5', 'Action B', 'success', 'Fin'),
  ],
};
async function setup(page: Page) {
  await page.route('**/api/**', (r) => r.fulfill({ json: [] }));
  await page.route('**/api/v1/tools/ai/assistant/options*', (r) =>
    r.fulfill({ json: { defaultProvider: 'none', models: [] } }),
  );
  await page.route('**/api/v1/auth/status', (r) =>
    r.fulfill({
      json: { setupRequired: false, user: { id: 'admin', username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({ json: [{ id: 'base', version: '1.0.0', name: 'Base', components }] }),
  );
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({ json: { definition: flow, publishedVersion: 1, backupStatus: 'ready' } }),
  );
  await page.goto('/flows/11111111-1111-4111-8111-111111111111/edit');
  await expect(page.locator('.step-card')).toHaveCount(5);
}
test('guided and canvas share additions, deletion, undo and selections', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(6);
  await page.getByRole('button', { name: 'Actions de Action A — Copie', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await page.getByRole('button', { name: 'Annuler la dernière modification', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(6);
  await page.getByRole('button', { name: 'Rétablir la modification', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  expect(errors).toEqual([]);
});
test('connections can be removed, dragged and observed in both views', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  const link = page.getByRole('button', { name: 'Liaison success → Fin', exact: true }).first();
  await link.click();
  await page.getByRole('menuitem', { name: 'Supprimer la liaison', exact: true }).click();
  const source = page.locator('.canvas-node[data-node-id="Action A"] .output');
  const target = page.locator('.canvas-node[data-node-id="Fin"] .input');
  await source.scrollIntoViewIfNeeded();
  const a = await source.boundingBox();
  await source.dispatchEvent('pointerdown', {
    pointerId: 1,
    pointerType: 'mouse',
    button: 0,
    clientX: a!.x + 14,
    clientY: a!.y + 14,
  });
  await target.scrollIntoViewIfNeeded();
  const b = await target.boundingBox();
  await page.mouse.move(b!.x + 14, b!.y + 14);
  await page.mouse.down();
  await page.mouse.up();
  await expect(
    page.getByRole('button', { name: 'Liaison success → Fin', exact: true }),
  ).toHaveCount(2);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await expect(
    page.locator('.continuation-label').filter({ hasText: '2 liaisons entrantes' }),
  ).toBeVisible();
});
test('AI uses the widget and only applies a reviewed proposal', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await setup(page);
  await page.route('**/api/v1/flows/ai/refactor', (r) =>
    r.fulfill({
      json: {
        success: true,
        requestId: 'proposal',
        flow: { ...flow, nodes: [...flow.nodes, node('Proposition', 'action')] },
      },
    }),
  );
  await page.getByRole('button', { name: "Ouvrir l'assistant IA", exact: true }).click();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await expect(panel).toBeVisible();
  await expect(panel).toContainText('Flow complet : Flow de test');
  await panel.locator('textarea').fill('Ajouter une action');
  await panel.getByRole('button', { name: 'Préparer les changements', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Appliquer au flow', exact: true })).toBeVisible();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await panel.getByRole('button', { name: 'Appliquer au flow', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(6);
  expect(errors).toEqual([]);
});
test('guided branches keep their widths inside a horizontally scrollable workspace', async ({
  page,
}) => {
  await setup(page);
  const cards = await page
    .locator('.step-card')
    .evaluateAll((es) => es.map((e) => e.getBoundingClientRect().width));
  expect(Math.min(...cards)).toBeGreaterThanOrEqual(430);
  const dimensions = await page.evaluate(() => ({
    body: document.documentElement.scrollWidth,
    viewport: innerWidth,
  }));
  expect(dimensions.body).toBeLessThanOrEqual(dimensions.viewport);
  const violations = (
    await new AxeBuilder({ page })
      .include('app-flow-graph-editor')
      .withTags(['wcag2a', 'wcag2aa'])
      .analyze()
  ).violations;
  expect(violations).toEqual([]);
});

test('five nested decisions retain card widths, a single convergence and local scrolling', async ({
  page,
}, testInfo) => {
  await setup(page);
  const deepNodes = [
    node('Entrée', 'base-input-file'),
    ...Array.from({ length: 5 }, (_, i) => [
      node('Décision ' + (i + 1), 'decision'),
      node('Alternative ' + (i + 1), 'action'),
    ]).flat(),
    node('Principale', 'action'),
    node('Fin', 'finish'),
  ];
  const deepEdges = [
    edge('deep-entry', 'Entrée', 'success', 'Décision 1'),
    ...Array.from({ length: 5 }, (_, i) => [
      edge('yes' + i, 'Décision ' + (i + 1), 'yes', i === 4 ? 'Principale' : 'Décision ' + (i + 2)),
      edge('no' + i, 'Décision ' + (i + 1), 'no', 'Alternative ' + (i + 1)),
      edge('join' + i, 'Alternative ' + (i + 1), 'success', 'Fin'),
    ]).flat(),
    edge('main-join', 'Principale', 'success', 'Fin'),
  ];
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({
      json: {
        definition: { ...flow, nodes: deepNodes, connections: deepEdges },
        publishedVersion: 1,
        backupStatus: 'ready',
      },
    }),
  );
  await page.reload();
  await expect(page.locator('.step-card')).toHaveCount(13);
  const widths = await page
    .locator('.step-card')
    .evaluateAll((es) => es.map((e) => e.getBoundingClientRect().width));
  expect(Math.min(...widths)).toBeGreaterThanOrEqual(430);
  await expect(page.locator('.step-card[data-node-id="Fin"]')).toHaveCount(1);
  const sizes = await page.locator('.workbench-canvas').evaluate((el) => ({
    client: el.clientWidth,
    scroll: el.scrollWidth,
    body: document.documentElement.scrollWidth,
    window: innerWidth,
  }));
  expect(sizes.scroll).toBeGreaterThan(sizes.client);
  expect(sizes.body).toBeLessThanOrEqual(sizes.window);
  await page.locator('.step-card[data-node-id="Décision 5"]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('guided-five-levels.png') });
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(13);
  await page.getByRole('button', { name: 'Tout afficher', exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath('canvas-five-levels.png') });
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  const before = await page.locator('.workbench-canvas').evaluate((el) => el.scrollWidth);
  await page.locator('mat-expansion-panel-header').first().click();
  await expect
    .poll(() => page.locator('.workbench-canvas').evaluate((el) => el.scrollWidth))
    .toBeLessThan(before);
});

test('stale AI proposals cannot overwrite changes made while reviewing', async ({ page }) => {
  await setup(page);
  await page.route('**/api/v1/flows/ai/refactor', (r) =>
    r.fulfill({
      json: {
        success: true,
        requestId: 'stale',
        flow: { ...flow, nodes: [...flow.nodes, node('Proposition obsolète', 'action')] },
      },
    }),
  );
  await page.getByRole('button', { name: "Ouvrir l'assistant IA", exact: true }).click();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await panel.locator('textarea').fill('Modifier');
  await panel.getByRole('button', { name: 'Préparer les changements', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Appliquer au flow', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant', exact: true }).click();
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  await panel.getByRole('button', { name: 'Appliquer au flow', exact: true }).click();
  await expect(page.locator('.step-card[data-node-id="Proposition obsolète"]')).toHaveCount(0);
  await expect(page.locator('.step-name').filter({ hasText: 'Action A — Copie' })).toHaveCount(1);
  await expect(page.getByText(/Le flow a changé depuis la proposition/)).toBeVisible();
});

test('right-click and stationary touch hold expose the same step actions', async ({ page }) => {
  await setup(page);
  const card = page.locator('.step-card[data-node-id="Action A"]');
  await card.scrollIntoViewIfNeeded();
  await card.click({ button: 'right' });
  await expect(page.getByRole('menuitem', { name: 'Dupliquer', exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
  const rect = await card.boundingBox();
  await card.dispatchEvent('pointerdown', {
    pointerType: 'touch',
    pointerId: 3,
    button: 0,
    clientX: rect!.x + 50,
    clientY: rect!.y + 65,
  });
  await expect(page.getByRole('menuitem', { name: 'Dupliquer', exact: true })).toBeVisible();
  await card.dispatchEvent('pointerup', { pointerType: 'touch', pointerId: 3 });
  await page.keyboard.press('Escape');
});

test('the general assistant retains its draft when the shared widget is closed and reopened', async ({
  page,
}) => {
  await setup(page);
  await page.goto('/');
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  const panel = page.getByLabel('Panneau de l’assistant IA', { exact: true });
  await expect(panel.locator('app-ai-assistant-page')).toBeVisible();
  const composer = panel.locator('textarea').first();
  await composer.fill('Mon brouillon conservé');
  await page.getByRole('button', { name: 'Fermer le panneau de l’assistant', exact: true }).click();
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
  await expect(composer).toHaveValue('Mon brouillon conservé');
  expect(errors).toEqual([]);
});

test('canvas placement stays fixed when a connection changes or the view switches', async ({
  page,
}) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  const positions = () =>
    page
      .locator('.canvas-node')
      .evaluateAll((es) =>
        Object.fromEntries(
          es.map((e) => [
            (e as HTMLElement).dataset['nodeId'],
            (e as HTMLElement).style.left + ',' + (e as HTMLElement).style.top,
          ]),
        ),
      );
  const before = await positions();
  await page.getByRole('button', { name: 'Liaison success → Décision', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer la liaison', exact: true }).click();
  expect(await positions()).toEqual(before);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  expect(await positions()).toEqual(before);
  const source = page.locator('.canvas-node[data-node-id="Entrée"] .output');
  await source.click();
  await page.locator('.canvas-node[data-node-id="Décision"] .input').click();
  await expect(
    page.getByRole('button', { name: 'Liaison success → Décision', exact: true }),
  ).toHaveCount(1);
  expect(await positions()).toEqual(before);
});

test('canvas exposes accessible controls and connection actions', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  const violations = (
    await new AxeBuilder({ page })
      .include('app-flow-canvas')
      .withTags(['wcag2a', 'wcag2aa'])
      .analyze()
  ).violations;
  expect(violations).toEqual([]);
});
