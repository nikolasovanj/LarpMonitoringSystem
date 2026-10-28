import { test, expect } from '../support/fixtures';
import { openApp, reading } from '../support/mock';

test.describe('live connection', () => {
  test('reconnects after the connection drops, refetches, and live data flows again', async ({ page, mock }) => {
    await openApp(page, mock);
    const before = (await mock.state()).requests.filter((r) => r.url === '/api/devices').length;
    const temp = page.locator('.device', { hasText: 'pump01' }).locator('.tile', { hasText: 'temperature' });

    await mock.dropConnections();

    await expect(page.locator('.conn')).toHaveText('Reconnecting…');
    await expect(page.locator('.conn')).toHaveText('Live', { timeout: 15_000 });

    // everything that may have been missed is refetched
    await expect.poll(async () => (await mock.state()).requests.filter((r) => r.url === '/api/devices').length)
      .toBeGreaterThan(before);

    await mock.waitForHub();
    await mock.emit('Telemetry', [reading({ value: 77.77 })]);
    await expect(temp.locator('.tile-value')).toContainText('77.77');
  });

  test('keeps trying while the API is down and connects when it comes back', async ({ page, mock }) => {
    // Refuse the negotiation request, the way an unreachable API would.
    let blocked = true;
    await page.route('**/hubs/live/negotiate*', (route) => (blocked ? route.abort('connectionrefused') : route.continue()));

    await page.goto('/');
    await expect(page.locator('.conn')).toHaveText('Reconnecting…');
    expect((await mock.state()).sockets).toBe(0);

    blocked = false;
    await expect(page.locator('.conn')).toHaveText('Live', { timeout: 15_000 });
    await mock.waitForHub();
  });

  test('keeps the last known values on screen while disconnected', async ({ page, mock }) => {
    await openApp(page, mock);
    const temp = page.locator('.device', { hasText: 'pump01' }).locator('.tile', { hasText: 'temperature' });
    await mock.emit('Telemetry', [reading({ value: 64.5 })]);
    await expect(temp.locator('.tile-value')).toContainText('64.50');

    await page.route('**/hubs/live/negotiate*', (route) => route.abort('connectionrefused'));
    await mock.dropConnections();

    await expect(page.locator('.conn')).not.toHaveText('Live');
    await expect(temp.locator('.tile-value')).toContainText('64.50');
  });

  test('the indicator and the navigation are present on every page', async ({ page, mock }) => {
    for (const path of ['/', '/alerts', '/devices/site1/line1/pump01']) {
      await openApp(page, mock, path);
      await expect(page.locator('.conn')).toHaveText('Live');
      await expect(page.getByRole('link', { name: 'Overview' }).first()).toBeVisible();
    }
  });
});
