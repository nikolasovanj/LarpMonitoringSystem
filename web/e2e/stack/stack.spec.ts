import { expect, test, type Page } from '@playwright/test';

const DEVICE = process.env.STACK_DEVICE ?? 'pump01';
const SITE = process.env.STACK_SITE ?? 'site1';
const LINE = process.env.STACK_LINE ?? 'line1';

const card = (page: Page) => page.locator('.device', { hasText: DEVICE });
const faultPanel = (page: Page) =>
  page.locator('section.card', { has: page.getByRole('heading', { name: 'Inject a fault' }) });

test.describe('real stack', () => {
  test('the dashboard connects and shows the simulated device as online', async ({ page }) => {
    await page.goto('/');
    await expect(page.locator('.conn')).toHaveText('Live');
    await expect(card(page)).toBeVisible();
    await expect(card(page).locator('.status-online')).toBeVisible();
    await expect(card(page).locator('.tile').first()).toBeVisible();
  });

  test('live values keep changing while the simulator publishes', async ({ page }) => {
    await page.goto('/');
    const value = card(page).locator('.tile', { hasText: 'temperature' }).locator('.tile-value');
    await expect(value).toContainText(/\d/);
    const first = await value.innerText();

    // The simulator adds noise to every sample, so the displayed value must move.
    await expect.poll(() => value.innerText(), { timeout: 30_000 }).not.toBe(first);
    await expect(card(page).locator('.tile', { hasText: 'temperature' }).locator('time')).toHaveText(/just now|\ds ago/);
  });

  test('a fault injected from the UI reaches the device, and its reply comes back', async ({ page }) => {
    await page.goto(`/devices/${SITE}/${LINE}/${DEVICE}`);
    await expect(page.locator('.conn')).toHaveText('Live');
    const form = faultPanel(page);

    await form.getByLabel('Sensor').selectOption('pressure');
    await form.getByLabel('Fault').selectOption('spike');
    await form.getByRole('button', { name: 'Inject' }).click();

    // Whole chain: API -> broker -> simulator -> cmd/result -> API gateway -> hub -> this page.
    await expect(form.getByText('The device accepted the command.')).toBeVisible();
  });

  test('a command the device cannot run is rejected by the device, with its reason', async ({ page, request }) => {
    await page.goto('/');
    await expect(page.locator('.conn')).toHaveText('Live');

    // The UI only offers sensors the device has, so go through the API directly.
    const res = await request.post(`/api/devices/${SITE}/${LINE}/${DEVICE}/faults`, {
      data: { sensor: 'no_such_sensor', fault: 'spike' },
    });
    expect(res.status()).toBe(202); // the API only checks the command's shape

    await expect(page.locator('.toast-command-failed')).toContainText('no sensor');
  });

  test('history shows data for the last hour', async ({ page }) => {
    await page.goto(`/devices/${SITE}/${LINE}/${DEVICE}`);
    const history = page.locator('section.card', { has: page.getByRole('heading', { name: 'History' }) });
    await expect(history.locator('canvas')).toBeVisible();
    await expect(history.getByText(/\d+ points/)).toBeVisible();
  });

  test('the alerts page loads from the real API', async ({ page }) => {
    await page.goto('/alerts');
    await expect(page.getByRole('heading', { name: 'Alerts' })).toBeVisible();
    // Either rows or the empty state, but never an error.
    await expect(page.getByRole('alert')).toHaveCount(0);
    await expect(page.locator('tbody tr, .empty').first()).toBeVisible();
  });
});
