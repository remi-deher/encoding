import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  await page.route('**/api/**', (route) => route.fulfill({ json: [] }));
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      json: {
        setupRequired: false,
        user: { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai', (route) =>
    route.fulfill({
      json: ['codex-cli', 'claude-cli'].map((id) => ({
        id,
        name: id,
        category: 'CLI',
        description: 'CLI',
        isInstalled: true,
        capabilities: [],
        authStatus: { toolId: id, state: 'unconfigured' },
      })),
    }),
  );
  await page.route('**/api/v1/tools/ai/settings', (route) =>
    route.fulfill({
      json: {
        defaultProvider: 'ollama',
        ollamaBaseUrl: 'http://localhost:11434/v1/',
        ollamaModel: 'llama3.2',
        openAiBaseUrl: 'https://api.openai.com/v1/',
        openAiModel: 'gpt-4o-mini',
        hasOpenAiApiKey: true,
        timeoutSeconds: 120,
        subtitleBatchSize: 80,
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/cluster', (route) =>
    route.fulfill({
      json: {
        nodes: [
          { nodeId: 'first', name: 'Salon', providers: ['ollama'] },
          { nodeId: 'second', name: 'Bureau', providers: ['codex-cli'] },
        ],
      },
    }),
  );
});

test('provider settings remain focused, accessible and usable in both themes', async ({
  page,
}, testInfo) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/settings?tab=ai');
  const tab = page.locator('app-ai-tools-tab');
  await expect(tab.getByRole('heading', { name: 'IA & fournisseurs' })).toBeVisible();
  await expect(tab.getByLabel('Adresse du serveur')).toBeVisible();
  await expect(tab.getByLabel('Clé API', { exact: true })).toHaveCount(0);
  await tab.getByRole('button', { name: /OpenAI \/ API compatible/ }).click();
  await expect(tab.getByLabel('Clé API', { exact: true })).toHaveAttribute('type', 'password');
  await expect(tab.getByLabel('Adresse du serveur')).toHaveCount(0);
  await expect(tab.getByRole('combobox', { name: 'Moteur du cluster' })).toContainText('Ollama');
  await expect(tab.getByText('Simuler', { exact: false })).toHaveCount(0);
  for (const theme of ['light', 'dark']) {
    await page.evaluate(
      (value) => document.documentElement.setAttribute('data-theme', value),
      theme,
    );
    const result = await new AxeBuilder({ page })
      .include('app-ai-tools-tab')
      .withTags(['wcag2a', 'wcag2aa', 'wcag21aa'])
      .analyze();
    expect(result.violations).toEqual([]);
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await page.screenshot({
      path: testInfo.outputPath(`ai-providers-${theme}.png`),
      fullPage: true,
    });
  }
  expect(errors).toEqual([]);
});

test('Codex only shows the supplied code and closing cancels the attempt', async ({ page }) => {
  let cancelled = false;
  await page.route('**/api/v1/tools/ai/codex-cli/auth/device-code/start', (route) =>
    route.fulfill({
      json: {
        toolId: 'codex-cli',
        prompt: {
          userCode: 'ABCD-12345',
          verificationUri: 'https://auth.openai.com/codex/device',
          expiresInSeconds: 60,
          intervalSeconds: 5,
        },
      },
    }),
  );
  await page.route('**/api/v1/tools/ai/codex-cli/auth/device-code/status', (route) =>
    route.fulfill({ json: { toolId: 'codex-cli', state: 'pending' } }),
  );
  await page.route('**/api/v1/tools/ai/codex-cli/auth/cancel', async (route) => {
    cancelled = true;
    await route.fulfill({ json: {} });
  });
  await page.goto('/settings?tab=ai');
  await page
    .locator('app-ai-tools-tab')
    .getByRole('button', { name: /Codex CLI/ })
    .click();
  await page.getByRole('button', { name: 'Associer mon compte', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText('ABCD-12345')).toBeVisible();
  await expect(dialog.getByRole('link')).toHaveAttribute(
    'href',
    'https://auth.openai.com/codex/device',
  );
  await expect(dialog.getByText(/Simuler/)).toHaveCount(0);
  await dialog.getByRole('button', { name: 'Fermer', exact: true }).click();
  await expect.poll(() => cancelled).toBe(true);
});
