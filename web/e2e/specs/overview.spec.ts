import { test, expect } from '../support/fixtures';
import { openApp, reading } from '../support/mock';

const stat = (page: import('@playwright/test').Page, label: string) =>
  page.locator('.stat', { has: page.locator('.stat-label', { hasText: new RegExp(`^${label}$`) }) }).locator('.stat-value');

test.describe('overview', () => {
  test('shows summary counts and devices grouped by site and line', async ({ page, mock }) => {
    await openApp(page, mock);

    await expect(page.getByRole('heading', { name: 'Plant overview' })).toBeVisible();
    await expect(stat(page, 'Devices')).toHaveText('2');
    await expect(stat(page, 'Online')).toHaveText('1');
    await expect(stat(page, 'Offline')).toHaveText('1');
    await expect(stat(page, 'Open alerts')).toHaveText('0');

    await expect(page.getByRole('heading', { name: 'site1 · line1' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'site1 · line2' })).toBeVisible();

    const pump = page.locator('.device', { hasText: 'pump01' });
    await expect(pump.locator('.status-online')).toBeVisible();
    await expect(pump.locator('.tile')).toHaveCount(2);
    await expect(pump.locator('.tile', { hasText: 'temperature' }).locator('.tile-value')).toContainText('70.12');

    const motor = page.locator('.device', { hasText: 'motor02' });
    await expect(motor.locator('.status-offline')).toBeVisible();
  });

  test('shows a hint when no device has reported yet', async ({ page, mock }) => {
    await mock.setDevices([]);
    await openApp(page, mock);
    await expect(page.getByText('No devices yet')).toBeVisible();
    await expect(stat(page, 'Devices')).toHaveText('0');
  });

  test('a live reading replaces the fetched value and the card stays on the right sensor', async ({ page, mock }) => {
    await openApp(page, mock);
    const temp = page.locator('.device', { hasText: 'pump01' }).locator('.tile', { hasText: 'temperature' });
    const pressure = page.locator('.device', { hasText: 'pump01' }).locator('.tile', { hasText: 'pressure' });

    await mock.emit('Telemetry', [reading({ value: 83.25 })]);

    await expect(temp.locator('.tile-value')).toContainText('83.25');
    await expect(pressure.locator('.tile-value')).toContainText('6.01'); // untouched
  });

  test('keeps the newest value when a batch arrives out of order', async ({ page, mock }) => {
    await openApp(page, mock);
    const temp = page.locator('.device', { hasText: 'pump01' }).locator('.tile', { hasText: 'temperature' });
    const now = Date.now();

    await mock.emit('Telemetry', [
      reading({ value: 90, time: new Date(now).toISOString() }),
      reading({ value: 10, time: new Date(now - 5000).toISOString() }),
    ]);

    await expect(temp.locator('.tile-value')).toContainText('90.00');
  });

  test('a pushed status change updates the badge and the counters without a refetch', async ({ page, mock }) => {
    await openApp(page, mock);
    const pump = page.locator('.device', { hasText: 'pump01' });
    await expect(pump.locator('.status-online')).toBeVisible();

    await mock.emit('DeviceStatus', { site: 'site1', line: 'line1', device: 'pump01', status: 'offline', time: new Date().toISOString() });

    await expect(pump.locator('.status-offline')).toBeVisible();
    await expect(stat(page, 'Online')).toHaveText('0');
    await expect(stat(page, 'Offline')).toHaveText('2');
  });

  test('shows an error message when the API cannot be reached', async ({ page }) => {
    await page.route('**/api/devices', (route) => route.abort('connectionrefused'));
    await page.goto('/');
    await expect(page.getByRole('alert')).toContainText('Cannot reach the API');
  });

  test('a device card links to its detail page', async ({ page, mock }) => {
    await openApp(page, mock);
    await page.getByRole('link', { name: 'pump01' }).click();
    await expect(page).toHaveURL(/\/devices\/site1\/line1\/pump01$/);
    await expect(page.getByRole('heading', { name: 'pump01' })).toBeVisible();
  });
});
