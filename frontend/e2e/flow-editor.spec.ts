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
  const link = page.getByRole('button', { name: 'Liaison success vers Fin', exact: true }).first();
  await link.focus();
  await link.press('Enter');
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
    page.getByRole('button', { name: 'Liaison success vers Fin', exact: true }),
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
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
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
  await expect(
    page.locator('mat-expansion-panel', { has: page.locator('.branch-heading') }),
  ).toHaveCount(0);
  const branch = page.getByRole('button', { name: 'Replier le chemin Oui', exact: true });
  await expect(branch).toHaveAttribute('aria-expanded', 'true');
  await branch.click();
  await expect(
    page.getByRole('button', { name: 'Déplier le chemin Oui', exact: true }),
  ).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('.branch-lane').first().locator('.step-card')).toHaveCount(0);
  await page.getByRole('button', { name: 'Déplier le chemin Oui', exact: true }).click();
  await expect(page.locator('.branch-lane').first().locator('.step-card')).toHaveCount(1);
  await expect(page.locator('.link-insertion').first()).toContainText('Étape');
  const cards = await page
    .locator('.step-card')
    .evaluateAll((es) => es.map((e) => e.getBoundingClientRect().width));
  expect(Math.min(...cards)).toBeGreaterThanOrEqual(338);
  expect(Math.max(...cards)).toBeLessThanOrEqual(440);
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
  expect(Math.min(...widths)).toBeGreaterThanOrEqual(338);
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
  await page
    .getByRole('button', { name: /^Replier le chemin / })
    .first()
    .click();
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
  await page.getByRole('button', { name: 'Ouvrir l’assistant IA', exact: true }).click();
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
  await expect(page.locator('.canvas-node')).toHaveCount(5);
  const before = await positions();
  await page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }).focus();
  await page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Supprimer la liaison', exact: true }).click();
  await expect.poll(positions).toEqual(before);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  await expect.poll(positions).toEqual(before);
  const source = page.locator('.canvas-node[data-node-id="Entrée"] .output');
  await source.click();
  await page.locator('.canvas-node[data-node-id="Décision"] .input').click();
  await expect(
    page.getByRole('button', { name: 'Liaison success vers Décision', exact: true }),
  ).toHaveCount(1);
  await expect.poll(positions).toEqual(before);
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

test('editor keeps navigation above the vertical flow and opens tools on demand', async ({
  page,
}) => {
  await setup(page);
  const header = page.getByRole('toolbar', { name: 'Commandes du flow', exact: true });
  await expect(header.getByRole('button', { name: 'Guidé', exact: true })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await expect(
    header.getByRole('button', { name: "Ouvrir l'assistant IA", exact: true }),
  ).toHaveCount(0);
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
  await header.getByRole('button', { name: 'Canvas', exact: true }).click();
  await expect(
    page
      .getByRole('toolbar', { name: 'Navigation du canvas' })
      .getByRole('button', { name: 'Tout afficher', exact: true }),
  ).toBeVisible();
  expect((await page.locator('.connection-list').boundingBox())?.height).toBeLessThanOrEqual(1);
  await header.getByRole('button', { name: 'Autres actions du flow', exact: true }).click();
  await page
    .getByRole('menuitem', { name: 'Basculer le volet de simulation', exact: true })
    .click();
  await expect(
    page.getByRole('button', { name: 'Vérification du flow', exact: true }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Réduire', exact: true }).click();
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
});

async function dragPaletteAction(
  page: Page,
  target: () => Promise<{ x: number; y: number }>,
  name = 'action',
) {
  if (await page.locator('.palette-dock_collapsed').count()) {
    await page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true }).click();
  }
  const card = page.locator('.component-card').filter({
    has: page.locator('.component-title').filter({ hasText: new RegExp('^' + name + '$') }),
  });
  await card.scrollIntoViewIfNeeded();
  const rect = (await card.boundingBox())!;
  await page.mouse.move(rect.x + 40, rect.y + 30);
  await page.mouse.down();
  await page.mouse.move(rect.x + 48, rect.y + 30, { steps: 4 });
  const point = await target();
  await page.mouse.move(point.x, point.y, { steps: 20 });
  await page.mouse.up();
}
async function guidedDropPoint(page: Page) {
  const port = page.locator('.step-card[data-node-id="Entrée"] .output-action');
  await port.scrollIntoViewIfNeeded();
  const rect = (await port.boundingBox())!;
  return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
}
test('palette drag inserts on guided outputs and supports undo', async ({ page }) => {
  await setup(page);
  await dragPaletteAction(page, () => guidedDropPoint(page));
  await expect(page.locator('.step-card')).toHaveCount(6);
  await expect(page.locator('.detached')).toHaveCount(0);
  await page.getByRole('button', { name: 'Annuler la dernière modification', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
});
test('palette drag places canvas blocks and cancels outside the workspace', async ({ page }) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  await dragPaletteAction(page, async () => {
    const rect = (await page.locator('.canvas-surface').boundingBox())!;
    return { x: rect.x + 90, y: rect.y + 280 };
  });
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  const added = page.locator('.canvas-node[data-node-id^="flow-node-"]');
  await expect(added).toHaveCSS('left', '90px');
  await expect(added).toHaveCSS('top', '280px');
  await dragPaletteAction(page, async () => ({ x: 25, y: 25 }));
  await expect(page.locator('.canvas-node')).toHaveCount(6);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.detached .step-card')).toHaveCount(1);
});
test('palette drag refuses a terminal block before an existing continuation', async ({ page }) => {
  await setup(page);
  await dragPaletteAction(page, () => guidedDropPoint(page), 'finish');
  await expect(page.locator('.step-card')).toHaveCount(5);
  await expect(
    page.getByText('Une étape terminale ne peut pas être insérée avant une suite.', {
      exact: false,
    }),
  ).toBeVisible();
});

test('palette drag accepts empty guided workspace without connecting the block', async ({
  page,
}) => {
  await setup(page);
  await dragPaletteAction(page, async () => {
    const rect = (await page.locator('.workbench-canvas').boundingBox())!;
    return { x: rect.x + 4, y: rect.y + 4 };
  });
  await expect(page.locator('.step-card')).toHaveCount(6);
  await expect(page.locator('.detached .step-card')).toHaveCount(1);
  await page.getByRole('button', { name: 'Annuler la dernière modification', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await expect(page.locator('.detached')).toHaveCount(0);
});

test('friendly editor opens the inspector beside the flow and closes the palette', async ({
  page,
}, info) => {
  await setup(page);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await expect(page.locator('app-flow-inline-parameters')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Vérifier', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true }).click();
  await page.locator('.step-card[data-node-id="Entrée"] .step-name').click();
  const inspector = page.getByRole('complementary', { name: 'Entrée', exact: true });
  await expect(inspector).toBeVisible();
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await expect(inspector.getByLabel('Nom du bloc', { exact: true })).toBeVisible();
  await page.screenshot({ path: info.outputPath('inspector.png'), fullPage: true });
  await page.evaluate(() => (document.documentElement.dataset['theme'] = 'dark'));
  await page.screenshot({ path: info.outputPath('inspector-dark.png'), fullPage: true });
  await inspector.getByRole('button', { name: 'Fermer le panneau', exact: true }).click();
  await expect(inspector).toBeHidden();
  await expect(page.locator('.step-card[data-node-id="Entrée"]')).toBeVisible();
});

test('palette closes after a click insertion and can be pinned for successive additions', async ({
  page,
}) => {
  await setup(page);
  const add = page.getByRole('button', { name: 'Basculer la palette de blocs', exact: true });
  await add.click();
  await page
    .locator('.component-card')
    .filter({ has: page.locator('.component-title').filter({ hasText: /^action$/ }) })
    .click();
  await expect(page.locator('.step-card')).toHaveCount(6);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(1);
  await add.click();
  await page
    .getByRole('button', { name: 'Garder la palette ouverte après un ajout', exact: true })
    .click();
  await page
    .locator('.component-card')
    .filter({ has: page.locator('.component-title').filter({ hasText: /^action$/ }) })
    .click();
  await expect(page.locator('.step-card')).toHaveCount(7);
  await expect(page.locator('.palette-dock_collapsed')).toHaveCount(0);
});

test('successful test displays the taken path on blocks without keeping a bottom panel', async ({
  page,
}, info) => {
  await setup(page);
  await page.route('**/api/v1/flows/simulate', (r) =>
    r.fulfill({
      json: {
        status: 'completed',
        detail: 'Parcours terminé',
        steps: [
          {
            nodeId: 'Entrée',
            name: 'Entrée',
            outputId: 'success',
            detail: 'Entrée parcourue',
            assumed: false,
            workerNodeId: 'worker',
            context: {
              extension: 'mkv',
              variables: {},
              beforeMetadata: {},
              currentMetadata: {},
              plan: [],
            },
          },
        ],
      },
    }),
  );
  await page.getByRole('button', { name: 'Tester', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Étape parcourue · Voir les détails du test', exact: true }),
  ).toBeVisible();
  await expect(page.locator('.simulation-tray')).toHaveCount(0);
  await page.screenshot({ path: info.outputPath('tested-flow.png'), fullPage: true });
  await page
    .getByRole('button', { name: 'Étape parcourue · Voir les détails du test', exact: true })
    .click();
  await expect(page.locator('.simulation-tray')).toBeVisible();
  await page.getByRole('button', { name: 'Réduire', exact: true }).click();
  await page.getByRole('button', { name: 'Actions de Action A', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Dupliquer', exact: true }).click();
  await expect(page.locator('app-flow-simulation-status button')).toHaveCount(0);
});

test('inspector preserves optional advanced settings while editing a common setting', async ({
  page,
}) => {
  await setup(page);
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [
        {
          id: 'base',
          version: '1.0.0',
          name: 'Base',
          components: components.map((c) =>
            c.id === 'action'
              ? {
                  ...c,
                  configurationSchema: {
                    version: 1,
                    fields: [
                      {
                        id: 'quality',
                        label: 'Qualité',
                        type: 'number',
                        required: true,
                        inline: true,
                        defaultValue: 20,
                      },
                      {
                        id: 'option',
                        label: 'Option avancée',
                        type: 'text',
                        defaultValue: 'Conserver',
                      },
                    ],
                  },
                }
              : c,
          ),
        },
      ],
    }),
  );
  await page.reload();
  const card = page.locator('.step-card[data-node-id="Action A"]');
  await expect(card).toContainText('Qualité : 20');
  await card.locator('.step-name').click();
  const inspector = page.getByRole('complementary', { name: 'Action A', exact: true });
  await inspector.getByLabel('Qualité', { exact: true }).fill('22');
  await expect(inspector.getByLabel('Option avancée', { exact: true })).toBeHidden();
  await inspector.getByText('Réglages avancés · 1', { exact: true }).click();
  await expect(inspector.getByLabel('Option avancée', { exact: true })).toHaveValue('Conserver');
  await inspector.getByRole('button', { name: 'Appliquer les réglages', exact: true }).click();
  await expect(card).toContainText('Qualité : 22');
});

test('canvas wires follow dragging before release and selection highlights connections', async ({
  page,
}) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  const entry = page.locator('.canvas-node').first();
  await entry.locator('.canvas-plugin').click();
  await expect(page.locator('.wire.highlighted')).not.toHaveCount(0);
  const wire = page.locator('.wire').first();
  const before = await wire.getAttribute('d');
  const handle = entry.getByRole('button', { name: 'Déplacer l’étape' });
  const rect = (await handle.boundingBox())!;
  await page.mouse.move(rect.x + rect.width / 2, rect.y + rect.height / 2);
  await page.mouse.down();
  await page.mouse.move(rect.x + rect.width / 2 + 60, rect.y + rect.height / 2 + 70, { steps: 12 });
  await expect(wire).not.toHaveAttribute('d', before!);
  expect(await page.evaluate(() => window.getSelection()?.toString())).toBe('');
  const during = await wire.getAttribute('d');
  await page.mouse.up();
  await expect(wire).toHaveAttribute('d', during!);
});

test('guided workspace fills the available width and separates blocks from its toolbar', async ({
  page,
}) => {
  await setup(page);
  const toolbar = (await page.locator('.command-bar').boundingBox())!;
  const workspace = (await page.locator('.flow-workbench').boundingBox())!;
  expect(Math.abs(toolbar.width - workspace.width)).toBeLessThan(2);
  const firstBlock = (await page.locator('.step-card').first().boundingBox())!;
  expect(firstBlock.y - toolbar.y - toolbar.height).toBeGreaterThanOrEqual(16);
});

test('inspector keeps deletion in the shared action footer', async ({ page }, info) => {
  await setup(page);
  await page.locator('.step-card[data-node-id="Action A"] .step-name').click();
  const inspector = page.getByRole('complementary', { name: 'Action A', exact: true });
  const footer = inspector.locator('.form-actions');
  await expect(footer.getByRole('button', { name: 'Supprimer ce bloc' })).toBeVisible();
  await expect(footer.getByRole('button', { name: 'Appliquer les réglages' })).toBeVisible();
  if (info.project.name === 'desktop') {
    const remove = (await footer.getByRole('button', { name: 'Supprimer ce bloc' }).boundingBox())!;
    const apply = (await footer
      .getByRole('button', { name: 'Appliquer les réglages' })
      .boundingBox())!;
    expect(Math.abs(remove.y + remove.height / 2 - apply.y - apply.height / 2)).toBeLessThan(2);
  }
  const applyButton = footer.getByRole('button', { name: 'Appliquer les réglages' });
  expect(
    await applyButton.evaluate((button) =>
      (() => {
        const text = button.querySelector('.mdc-button__label')!.getBoundingClientRect();
        const bounds = button.getBoundingClientRect();
        return (
          text.top >= bounds.top &&
          text.bottom <= bounds.bottom &&
          text.left >= bounds.left &&
          text.right <= bounds.right
        );
      })(),
    ),
  ).toBe(true);
  await page.screenshot({ path: info.outputPath('aligned-inspector.png') });
});

test('compact headings contain their actions and expose the common continuation', async ({
  page,
}, info) => {
  await setup(page);
  await expect(page.locator('.common-continuation')).toContainText('Suite commune : Fin');
  const contained = await page.locator('.step-card').evaluateAll((cards) =>
    cards.every((card) => {
      const bounds = card.getBoundingClientRect();
      return [...card.querySelectorAll('.step-heading button')].every((button) => {
        const b = button.getBoundingClientRect();
        return (
          b.left >= bounds.left &&
          b.right <= bounds.right &&
          b.top >= bounds.top &&
          b.bottom <= bounds.bottom
        );
      });
    }),
  );
  expect(contained).toBe(true);
  await page.getByRole('button', { name: 'Replier les branches', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(3);
  await page.getByRole('button', { name: 'Déplier les branches', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
  await page.screenshot({ path: info.outputPath('compact-guided.png') });
});

test('canvas layout is explicit, reversible and zoom stays inside the workspace', async ({
  page,
}, info) => {
  await setup(page);
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  const readPositions = () =>
    page.locator('.canvas-node').evaluateAll((cards) =>
      cards.map((card) => ({
        id: card.getAttribute('data-node-id'),
        left: (card as HTMLElement).style.left,
        top: (card as HTMLElement).style.top,
      })),
    );
  await expect(page.locator('.wire').first()).toHaveAttribute('d', /^M /);
  const before = await readPositions();
  await page.getByRole('button', { name: 'Disposition automatique', exact: true }).click();
  await expect(
    page.getByRole('button', { name: 'Annuler la disposition', exact: true }),
  ).toBeVisible();
  const placed = await page.locator('.canvas-node').evaluateAll((cards) =>
    cards.map((card) => {
      const e = card as HTMLElement;
      return {
        id: e.dataset['nodeId'],
        x: parseFloat(e.style.left),
        y: parseFloat(e.style.top),
        width: e.offsetWidth,
        height: e.offsetHeight,
      };
    }),
  );
  for (let i = 0; i < placed.length; i++)
    for (let j = i + 1; j < placed.length; j++) {
      const a = placed[i],
        b = placed[j];
      expect(
        a.x + a.width <= b.x ||
          b.x + b.width <= a.x ||
          a.y + a.height <= b.y ||
          b.y + b.height <= a.y,
      ).toBe(true);
    }
  await page.getByRole('button', { name: 'Annuler la disposition', exact: true }).click();
  await expect.poll(readPositions).toEqual(before);
  const canvas = page.locator('.canvas-viewport');
  const oldScale = await page.locator('.canvas-surface').getAttribute('style');
  await canvas.dispatchEvent('wheel', { deltaY: -100, ctrlKey: false });
  await expect(page.locator('.canvas-surface')).toHaveAttribute('style', oldScale!);
  const box = (await canvas.boundingBox())!;
  await canvas.dispatchEvent('wheel', {
    deltaY: -100,
    ctrlKey: true,
    clientX: box.x + 80,
    clientY: box.y + 100,
  });
  await expect(page.locator('.canvas-surface')).not.toHaveAttribute('style', oldScale!);
  await expect(page.getByRole('button', { name: 'Naviguer dans la minicarte' })).toBeVisible();
  await page.getByRole('button', { name: 'Tout afficher', exact: true }).click();
  await page.screenshot({ path: info.outputPath('compact-canvas.png') });
  const violations = (
    await new AxeBuilder({ page })
      .include('app-flow-canvas')
      .withTags(['wcag2a', 'wcag2aa'])
      .analyze()
  ).violations;
  expect(violations).toEqual([]);
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(5);
});

test('multiple exclusive cases, nested decisions and protected ends remain navigable in both views', async ({
  page,
}, info) => {
  await setup(page);
  const kinds = {
    ...component('subtitle-kind', 'condition', []),
    outputs: [
      { id: 'text', label: 'Texte', description: 'Sous-titres textuels' },
      { id: 'image', label: 'Image', description: 'Sous-titres image' },
      { id: 'absent', label: 'Absent', description: 'Aucune piste' },
    ],
  };
  const complexNodes = [
    node('Entrée', 'base-input-file'),
    node('Vidéo conforme ?', 'decision'),
    node('GPU utilisable ?', 'decision'),
    node('Encoder GPU', 'action'),
    node('Encoder CPU', 'action'),
    node('Audio conforme ?', 'decision'),
    node('Normaliser audio', 'action'),
    node('Sous-titres', 'subtitle-kind'),
    node('Convertir ASS', 'action'),
    node('Conserver PGS', 'action'),
    node('Sortie valide ?', 'decision'),
    node('Taille réduite ?', 'decision'),
    node('Succès', 'finish'),
    node('Conserver original', 'finish'),
    node('Échec protégé', 'finish'),
  ];
  const connections = [
    edge('c0', 'Entrée', 'success', 'Vidéo conforme ?'),
    edge('c1', 'Vidéo conforme ?', 'yes', 'Audio conforme ?'),
    edge('c2', 'Vidéo conforme ?', 'no', 'GPU utilisable ?'),
    edge('c3', 'GPU utilisable ?', 'yes', 'Encoder GPU'),
    edge('c4', 'GPU utilisable ?', 'no', 'Encoder CPU'),
    edge('c5', 'Encoder GPU', 'success', 'Audio conforme ?'),
    edge('c6', 'Encoder CPU', 'success', 'Audio conforme ?'),
    edge('c7', 'Audio conforme ?', 'yes', 'Sous-titres'),
    edge('c8', 'Audio conforme ?', 'no', 'Normaliser audio'),
    edge('c9', 'Normaliser audio', 'success', 'Sous-titres'),
    edge('c10', 'Sous-titres', 'text', 'Convertir ASS'),
    edge('c11', 'Sous-titres', 'image', 'Conserver PGS'),
    edge('c12', 'Sous-titres', 'absent', 'Sortie valide ?'),
    edge('c13', 'Convertir ASS', 'success', 'Sortie valide ?'),
    edge('c14', 'Conserver PGS', 'success', 'Sortie valide ?'),
    edge('c15', 'Sortie valide ?', 'yes', 'Taille réduite ?'),
    edge('c16', 'Sortie valide ?', 'no', 'Échec protégé'),
    edge('c17', 'Taille réduite ?', 'yes', 'Succès'),
    edge('c18', 'Taille réduite ?', 'no', 'Conserver original'),
  ];
  await page.route('**/api/v1/flows/components', (r) =>
    r.fulfill({
      json: [{ id: 'base', version: '1.0.0', name: 'Base', components: [...components, kinds] }],
    }),
  );
  await page.route('**/api/v1/flows/11111111-1111-4111-8111-111111111111/edit', (r) =>
    r.fulfill({
      json: {
        definition: { ...flow, nodes: complexNodes, connections },
        publishedVersion: 1,
        backupStatus: 'ready',
      },
    }),
  );
  await page.reload();
  await expect(page.locator('.step-card')).toHaveCount(15);
  for (const name of ['Texte', 'Image', 'Absent'])
    await expect(
      page.getByRole('button', { name: 'Replier le chemin ' + name, exact: true }),
    ).toHaveCount(1);
  for (const name of ['Audio conforme ?', 'Sous-titres', 'Sortie valide ?'])
    await expect(page.locator(`.step-card[data-node-id="${name}"]`)).toHaveCount(1);
  await page.locator('.step-card[data-node-id="Sous-titres"]').scrollIntoViewIfNeeded();
  await page.screenshot({ path: info.outputPath('complex-guided.png') });
  await page.getByRole('button', { name: 'Canvas', exact: true }).click();
  await page.getByRole('button', { name: 'Disposition automatique', exact: true }).click();
  await expect(page.locator('.canvas-node')).toHaveCount(15);
  await expect(page.locator('.wire')).toHaveCount(19);
  await expect
    .poll(() =>
      page.locator('.wire').evaluateAll((paths) => paths.every((p) => !!p.getAttribute('d'))),
    )
    .toBe(true);
  await page.screenshot({ path: info.outputPath('complex-canvas.png') });
  await page.getByRole('button', { name: 'Guidé', exact: true }).click();
  await expect(page.locator('.step-card')).toHaveCount(15);
});
