import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

const nodeId = '00000000-0000-0000-0000-000000000002';
const conversationId = '00000000000000000000000000000002.00000000000000000000000000000003';
const source = { kind: 'node', resourceId: nodeId, label: 'Nœud Salon', href: '/nodes' };
const conversation = {
  id: conversationId,
  provider: 'codex-cli',
  model: 'server-model-a',
  reasoningEffort: 'high',
  nodeId,
  title: 'Inspecter le nœud',
  source,
  createdAt: '2030-01-01T00:00:00Z',
  updatedAt: '2030-01-01T00:00:00Z',
};
const models = [
  {
    provider: 'codex-cli',
    model: 'server-model-a',
    label: 'Modèle fourni A',
    reasoningEfforts: ['low', 'high'],
    defaultReasoningEffort: 'high',
    reasoningKnown: true,
    nodeId,
    nodeName: 'Salon',
  },
  {
    provider: 'claude-cli',
    model: 'server-model-b',
    label: 'Modèle fourni B',
    reasoningEfforts: ['medium', 'max'],
    defaultReasoningEffort: 'medium',
    reasoningKnown: true,
    nodeId,
    nodeName: 'Salon',
  },
];

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: { setupRequired: false, user: { id: nodeId, username: 'admin', role: 'admin' } },
    }),
  );
  await page.route('**/health/details', (route) =>
    route.fulfill({ json: { status: 'healthy', checks: [] } }),
  );
  await page.route('**/api/v1/executions*', (route) => route.fulfill({ json: { executions: [] } }));
  await page.route('**/api/v1/tools/ai/assistant/options*', (route) =>
    route.fulfill({
      json: { defaultProvider: 'codex-cli', defaultModel: 'server-model-a', models },
    }),
  );
  await page.route('**/api/v1/tools/ai/assistant/conversations', (route) =>
    route.fulfill({ json: [conversation] }),
  );
  await page.route(`**/api/v1/tools/ai/assistant/conversations/${conversationId}`, (route) =>
    route.fulfill({
      json: {
        conversation,
        turns: [
          {
            requestId: 'old',
            userPrompt: 'Question initiale',
            content: 'Réponse initiale',
            selection: {
              provider: 'codex-cli',
              model: 'server-model-a',
              reasoningEffort: 'high',
              nodeId,
            },
            actualModel: 'confirmed-model-a',
          },
        ],
      },
    }),
  );
});

test('backend models and reasoning drive each turn while old turns retain observed models', async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  let release: (() => void) | undefined;
  let received: Record<string, unknown> | undefined;
  await page.route('**/api/v1/tools/ai/assistant/messages', async (route) => {
    const request = route.request().postDataJSON();
    received = request;
    await new Promise<void>((resolve) => {
      release = resolve;
    });
    await route.fulfill({
      json: {
        success: true,
        conversation: {
          ...conversation,
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
        },
        requestId: request.requestId,
        content: 'Nouvelle réponse',
        sources: [source],
        selection: {
          provider: request.provider,
          model: request.model,
          reasoningEffort: request.reasoningEffort,
          nodeId,
        },
        actualModel: 'confirmed-model-b',
      },
    });
  });
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  const model = assistant.getByRole('combobox', { name: 'Modèle', exact: true });
  await expect(model).toContainText('Modèle fourni A');
  await model.click();
  await page.getByRole('option', { name: /Modèle fourni B/ }).click();
  const reasoning = assistant.getByRole('combobox', { name: 'Effort de raisonnement' });
  await expect(reasoning).toContainText('Moyen');
  await reasoning.click();
  await expect(page.getByRole('option', { name: 'Faible', exact: true })).toHaveCount(0);
  await page.getByRole('option', { name: 'Maximum', exact: true }).click();
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Analyser ce nœud');
  await assistant.getByRole('button', { name: 'Envoyer', exact: true }).click();
  await expect.poll(() => received).toBeTruthy();
  expect(received).toMatchObject({
    provider: 'claude-cli',
    model: 'server-model-b',
    reasoningEffort: 'max',
    nodeId,
  });
  await expect(model).toBeDisabled();
  await expect(reasoning).toBeDisabled();
  release!();
  await expect(assistant.getByText('Nouvelle réponse', { exact: true })).toBeVisible();
  await expect(
    assistant.getByText('Modèle utilisé : confirmed-model-a', { exact: true }),
  ).toBeVisible();
  await expect(
    assistant.getByText('Modèle utilisé : confirmed-model-b', { exact: true }),
  ).toBeVisible();
  await expect(model).toBeEnabled();
  const result = await new AxeBuilder({ page })
    .include('app-ai-assistant-page')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(result.violations).toEqual([]);
  await model.scrollIntoViewIfNeeded();
  await page.screenshot({ path: testInfo.outputPath('assistant-models.png'), fullPage: true });
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth),
  ).toBeTruthy();
  expect(errors).toEqual([]);
});

test('unavailable catalog blocks a new turn and refresh preserves the selected conversation', async ({
  page,
}) => {
  await page.route('**/api/v1/tools/ai/assistant/options*', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'codex-cli',
        models: [],
        catalogs: [
          {
            nodeId,
            name: 'Salon',
            providers: [
              {
                provider: 'codex-cli',
                state: 'unavailable',
                models: [],
                observedAt: null,
                checkedAt: '2030-01-01T00:00:00Z',
                message: 'Catalogue indisponible',
              },
            ],
          },
        ],
      },
    }),
  );
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Question');
  await expect(assistant.getByRole('button', { name: 'Envoyer', exact: true })).toBeDisabled();
  await expect(assistant.getByText(/Salon · codex-cli : Catalogue indisponible/)).toBeVisible();
  await page.route('**/api/v1/tools/ai/assistant/options?refresh=true', (route) =>
    route.fulfill({ json: { defaultProvider: 'codex-cli', models } }),
  );
  await assistant.getByRole('button', { name: 'Actualiser les modèles' }).click();
  await expect(assistant.getByRole('combobox', { name: 'Modèle', exact: true })).toContainText(
    'Modèle fourni A',
  );
  await expect(assistant.getByRole('button', { name: 'Envoyer', exact: true })).toBeEnabled();
});

test('administrators disable discovered models through the backend policy', async ({ page }) => {
  let disabled = false;
  let saved: unknown;
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'codex-cli',
        ollamaBaseUrl: '',
        ollamaModel: '',
        openAiBaseUrl: '',
        openAiModel: '',
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
        assistantConversationRetentionDays: 90,
        assistantActivityRetentionDays: 30,
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/cluster', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/quotas', (route) => route.fulfill({ json: { nodes: [] } }));
  await page.route('**/api/v1/tools/ai/sessions?*', (route) =>
    route.fulfill({ json: { items: [], offset: 0, hasMore: false } }),
  );
  await page.route('**/api/v1/tools/ai/models?*', (route) =>
    route.fulfill({
      json: {
        nodes: [
          {
            nodeId,
            name: 'Salon',
            providers: [
              {
                provider: 'codex-cli',
                state: 'available',
                models: [
                  {
                    id: 'server-model-a',
                    label: 'Modèle fourni A',
                    reasoningEfforts: ['low', 'high'],
                    reasoningKnown: true,
                  },
                ],
                observedAt: '2030-01-01T00:00:00Z',
                checkedAt: '2030-01-01T00:00:00Z',
              },
            ],
          },
        ],
        policy: {
          disabledModels: disabled ? [{ provider: 'codex-cli', model: 'server-model-a' }] : [],
        },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/models/policy', async (route) => {
    saved = route.request().postDataJSON();
    disabled = !(saved as { enabled: boolean }).enabled;
    await route.fulfill({
      json: {
        disabledModels: disabled ? [{ provider: 'codex-cli', model: 'server-model-a' }] : [],
      },
    });
  });
  await page.goto('/settings?tab=ai');
  const panel = page.locator('app-ai-model-catalog');
  const toggle = panel.getByRole('switch', { name: 'Modèle fourni A' });
  await expect(toggle).toBeChecked();
  await toggle.click();
  await expect(toggle).not.toBeChecked();
  expect(saved).toEqual({ provider: 'codex-cli', model: 'server-model-a', enabled: false });
  await panel.getByRole('button', { name: 'Actualiser les modèles' }).click();
  await expect(toggle).not.toBeChecked();
  await toggle.click();
  await expect(toggle).toBeChecked();
  const result = await new AxeBuilder({ page })
    .include('app-ai-model-catalog')
    .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
    .analyze();
  expect(result.violations).toEqual([]);
});

test('network retries keep the selected model and request id while validation errors unlock selection', async ({
  page,
}) => {
  const requests: Record<string, unknown>[] = [];
  await page.route('**/api/v1/tools/ai/assistant/messages', async (route) => {
    requests.push(route.request().postDataJSON());
    await route.fulfill({
      status: requests.length === 1 ? 503 : 400,
      json: { title: requests.length === 1 ? 'Résultat incertain' : 'Ce modèle est désactivé' },
    });
  });
  await page.goto(`/ai/assistant?conversation=${conversationId}`);
  const assistant = page.locator('app-ai-assistant-page').first();
  const model = assistant.getByRole('combobox', { name: 'Modèle', exact: true });
  await model.click();
  await page.getByRole('option', { name: /Modèle fourni B/ }).click();
  await assistant.getByRole('textbox', { name: 'Votre question' }).fill('Question à reprendre');
  await assistant.getByRole('button', { name: 'Envoyer', exact: true }).click();
  await expect(
    assistant.getByRole('button', { name: 'Reprendre la demande', exact: true }),
  ).toBeVisible();
  await expect(model).toBeDisabled();
  await assistant.getByRole('button', { name: 'Reprendre la demande', exact: true }).click();
  await expect(model).toBeEnabled();
  expect(requests).toHaveLength(2);
  expect(requests[1]).toEqual(requests[0]);
  expect(requests[1]).toMatchObject({
    provider: 'claude-cli',
    model: 'server-model-b',
    reasoningEffort: 'medium',
    nodeId,
  });
  await expect(assistant.getByRole('textbox', { name: 'Votre question' })).toBeEnabled();
});
