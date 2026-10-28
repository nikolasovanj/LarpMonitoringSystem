import { test, expect } from '../support/fixtures';
import { openApp, reading } from '../support/mock';

const DEVICE = '/devices/site1/line1/pump01';

test.describe('device page', () => {
  test('shows one live chart per sensor, empty until data arrives', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);

    const cards = page.locator('section.card', { has: page.locator('h3', { hasText: /^(temperature|pressure)$/ }) });
    await expect(cards).toHaveCount(2);
    await expect(page.getByText('Waiting for live data…')).toHaveCount(2);

    const now = Date.now();
    await mock.emit('Telemetry', [
      reading({ value: 70.1, time: new Date(now - 2000).toISOString() }),
      reading({ value: 70.4, time: new Date(now - 1000).toISOString() }),
      reading({ value: 70.9, time: new Date(now).toISOString() }),
    ]);

    const temperature = cards.filter({ hasText: 'temperature' });
    await expect(temperature.locator('canvas')).toBeVisible();
    await expect(temperature.locator('.big')).toContainText('70.90');
    // the other sensor has received nothing
    await expect(cards.filter({ hasText: 'pressure' }).getByText('Waiting for live data…')).toBeVisible();
  });

  test('history loads for the first sensor and reloads for a new range', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);

    const history = page.locator('section.card', { has: page.getByRole('heading', { name: 'History' }) });
    await expect(history.getByText('60 points')).toBeVisible();
    await expect(history.locator('canvas')).toBeVisible();

    await history.getByRole('button', { name: '24 hours' }).click();
    await expect(history.getByRole('button', { name: '24 hours' })).toHaveClass(/active/);

    await expect.poll(async () => {
      const { requests } = await mock.state();
      const last = requests.filter((r) => r.url.includes('/history')).at(-1);
      if (!last) return 0;
      const q = new URL(last.url, 'http://x').searchParams;
      return (Date.parse(q.get('to')!) - Date.parse(q.get('from')!)) / 3_600_000;
    }).toBeCloseTo(24, 0);
  });

  test('switching the history sensor queries that series', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    const history = page.locator('section.card', { has: page.getByRole('heading', { name: 'History' }) });
    await expect(history.getByText('60 points')).toBeVisible();

    await history.getByLabel('Sensor').selectOption({ label: 'pressure' });

    await expect.poll(async () => {
      const { requests } = await mock.state();
      return requests.some((r) => r.url.startsWith('/api/series/2/history'));
    }).toBe(true);
  });

  test('lists only this device\'s alerts', async ({ page, mock }) => {
    const { alert } = await import('../support/mock');
    await mock.setAlerts([
      alert({ id: 1, message: 'pump alert' }),
      alert({ id: 2, device: 'motor02', line: 'line2', message: 'motor alert' }),
    ]);
    await openApp(page, mock, DEVICE);

    await expect(page.getByText('pump alert')).toBeVisible();
    await expect(page.getByText('motor alert')).toHaveCount(0);
  });

  test('an unknown device shows a not-found page', async ({ page, mock }) => {
    await openApp(page, mock, '/devices/site1/line1/ghost');
    await expect(page.getByRole('heading', { name: /Device "ghost" was not found/ })).toBeVisible();
    await page.getByRole('link', { name: 'Back to the overview' }).click();
    await expect(page).toHaveURL('/');
  });

  test('an unknown route shows the not-found page', async ({ page, mock }) => {
    await openApp(page, mock, '/nowhere');
    await expect(page.getByRole('heading', { name: /was not found/ })).toBeVisible();
  });

  test('breadcrumb link returns to the overview', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    await page.locator('.crumbs').getByRole('link', { name: 'Overview' }).click();
    await expect(page).toHaveURL('/');
  });
});
