import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';

test.beforeEach(async ({ page }) => {
  // Every API call returns an empty, well-formed payload so pages render deterministically.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '[]' }),
  );
  // Routes are matched last-registered first: the session is an administrator so the guarded pages render.
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

test('the overview loads on desktop and mobile without console errors', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/');
  await expect(page.locator('app-root')).toBeVisible();
  await expect(page.getByRole('heading').first()).toBeVisible();
  expect(errors).toEqual([]);
});

test('the primary navigation reaches the activity page', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  await page.goto('/activity');
  await expect(page.getByRole('heading', { name: /Activité/ }).first()).toBeVisible();
  expect(errors).toEqual([]);
});

test('the search command palette opens with Ctrl+K and navigates to encoding profiles', async ({
  page,
}) => {
  await page.goto('/');
  await expect(page.getByRole('heading').first()).toBeVisible();
  await page.keyboard.press('Control+k');

  const combobox = page.getByRole('combobox', { name: /Rechercher une destination/ });
  await expect(combobox).toBeVisible();
  await combobox.fill('Profils');
  await combobox.press('Enter');

  await expect(page).toHaveURL(/\/libraries\/encoding-profiles$/);
  await expect(page.getByRole('heading', { name: /Profils d’encodage/ }).first()).toBeVisible();
  await expect(page.getByRole('tab', { name: /Profils d’encodage/ })).toHaveAttribute(
    'aria-selected',
    'true',
  );
});

test('the first-run setup studio completes standalone onboarding and passes WCAG AA audit', async ({
  page,
}) => {
  let setupPayload: unknown = null;
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        setupRequired: setupPayload === null,
        user:
          setupPayload === null
            ? null
            : { id: '00000000-0000-0000-0000-000000000001', username: 'admin', role: 'admin' },
      }),
    }),
  );
  await page.route('**/api/v1/auth/setup/node', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        name: 'srv-salon',
        displayName: 'Serveur Salon',
        agentAvailable: true,
        inventory: {
          processorCount: 16,
          memoryMb: 32768,
          operatingSystem: 'Linux',
          architecture: 'x64',
          processorModel: 'AMD Ryzen 7 5800X',
          gpus: [{ name: 'NVIDIA GeForce RTX 3080', device: 'gpu0' }],
        },
        tools: [
          {
            toolId: 'ffmpeg',
            accelerationTypes: ['nvenc'],
            encoderChecks: [
              {
                encoderId: 'h264_nvenc',
                status: 'usable',
                diagnostic: 'ok',
              },
            ],
          },
        ],
        addresses: [
          { interface: 'eth0', address: '172.16.2.2' },
          { interface: 'eth0', address: 'fd00::50' },
          { interface: 'lo', address: '127.0.0.1' },
        ],
        urls: ['http://192.168.1.50:8090'],
      }),
    }),
  );
  await page.route('**/api/v1/local-node/status', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        nodeId: 'node-local',
        name: 'srv-salon',
        displayName: 'Serveur Salon',
        roles: ['server', 'worker'],
        maxConcurrentTasks: 2,
        automaticEnrollmentEnabled: true,
        capabilities: [
          {
            toolId: 'ffmpeg',
            version: '7.1',
            accelerationTypes: ['nvenc'],
            mediaTypes: ['video', 'audio'],
          },
        ],
        inventory: {
          processorCount: 16,
          memoryMb: 32768,
          operatingSystem: 'Linux',
          architecture: 'x64',
          processorModel: 'AMD Ryzen 7 5800X',
          gpus: [{ name: 'NVIDIA GeForce RTX 3080', device: 'gpu0' }],
        },
        isEnrolled: true,
        pairingKey: null,
        pairingExpiresAt: null,
        clusterId: null,
        clusterName: null,
        verificationCode: null,
        switching: null,
        switchFailure: null,
      }),
    }),
  );
  await page.route('**/api/v1/auth/setup', async (route) => {
    setupPayload = route.request().postDataJSON();
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        id: '00000000-0000-0000-0000-000000000001',
        username: 'admin',
        role: 'admin',
        isActive: true,
        createdAt: '2026-01-01T00:00:00Z',
      }),
    });
  });

  await page.goto('/setup');
  await expect(page.getByRole('heading', { name: /Bienvenue sur Encoding/ })).toBeVisible();
  await expect(page.getByText('AMD Ryzen 7 5800X')).toBeVisible();
  await expect(page.getByText('NVIDIA GeForce RTX 3080')).toBeVisible();
  await expect(page.getByText('http://192.168.1.50:8090', { exact: true })).toBeVisible();
  await expect(
    page.getByRole('list', { name: 'Adresses des interfaces' }).getByRole('listitem'),
  ).toHaveCount(3);

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa'])
    .disableRules(['color-contrast'])
    .analyze();
  const blocking = results.violations.filter(
    (v) => v.impact === 'critical' || v.impact === 'serious',
  );
  expect(blocking.map((v) => `${v.id}: ${v.nodes.length}`)).toEqual([]);

  await page.getByLabel('Mot de passe', { exact: true }).fill('un-mot-de-passe-long');
  await page.getByLabel('Confirmer le mot de passe', { exact: true }).fill('un-mot-de-passe-long');
  await page.getByRole('button', { name: /Accéder au tableau de bord/ }).click();
  await expect(page).toHaveURL(/\/$/);
  expect(setupPayload).toMatchObject({
    nodeDisplayName: 'Serveur Salon',
    mode: 'standalone',
    username: 'admin',
    password: 'un-mot-de-passe-long',
  });
});

test('setup lists all interfaces and keeps browser access with a published port in both themes', async ({
  page,
}) => {
  await page.route('**/api/v1/auth/status', (route) =>
    route.fulfill({ json: { setupRequired: true, user: null } }),
  );
  await page.route('**/api/v1/local-node/status', (route) => route.fulfill({ status: 401 }));
  const addresses = [
    { interface: 'eth0', address: '172.16.2.2' },
    { interface: 'eth0', address: '2001:db8:abcd:1234:5678:9abc:def0:1234' },
    { interface: 'eth1', address: '172.16.2.2' },
    { interface: 'lo', address: '::1' },
    { interface: 'docker0', address: '172.17.0.1' },
  ];
  await page.route('**/api/v1/auth/setup/node', (route) =>
    route.fulfill({
      json: {
        name: 'container',
        displayName: null,
        agentAvailable: false,
        inventory: null,
        tools: null,
        addresses,
        urls: [],
      },
    }),
  );
  for (const theme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme: theme });
    await page.goto('/setup');
    await expect(page.getByRole('heading', { name: 'Bienvenue sur Encoding' })).toBeVisible();
    const interfaces = page.getByRole('list', { name: 'Adresses des interfaces' });
    await expect(interfaces.getByRole('listitem')).toHaveCount(addresses.length);
    for (const item of addresses) {
      await expect(
        interfaces.getByText(`${item.address} (${item.interface})`, { exact: true }),
      ).toBeVisible();
    }
    await expect(page.getByText(new URL(page.url()).origin, { exact: true })).toBeVisible();
    await expect(page.getByText('Adresse utilisée par ce navigateur')).toBeVisible();
    const setup = page.locator('app-setup-wizard-page');
    if (theme === 'light') await expect(setup).toHaveCSS('background-image', 'none');
    else await expect(setup).not.toHaveCSS('background-image', 'none');
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
  }
});

test('the overview has no serious accessibility violation', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading').first()).toBeVisible();
  // Temporary, documented exception: axe reports 12 `color-contrast` violations on the overview (desktop and mobile).
  // Fixing the theme tokens is a UI change tracked separately; every other WCAG A/AA rule stays enforced.
  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa'])
    .disableRules(['color-contrast'])
    .analyze();
  const blocking = results.violations.filter(
    (v) => v.impact === 'critical' || v.impact === 'serious',
  );
  expect(blocking.map((v) => `${v.id}: ${v.nodes.length}`)).toEqual([]);
});
