import { test, expect } from '../support/fixtures';
import { alert, openApp } from '../support/mock';

test.describe('alerts', () => {
  test('lists open alerts with severity, device and state', async ({ page, mock }) => {
    await mock.setAlerts([
      alert({ id: 1, severity: 'critical', message: 'High temperature' }),
      alert({ id: 2, severity: 'info', message: 'Statistical anomaly', sensor: 'pressure' }),
    ]);
    await openApp(page, mock, '/alerts');

    const rows = page.locator('tbody tr');
    await expect(rows).toHaveCount(2);
    const first = rows.filter({ hasText: 'High temperature' });
    await expect(first.locator('.sev-critical')).toBeVisible();
    await expect(first.getByRole('link', { name: 'pump01' })).toBeVisible();
    await expect(first.locator('.state-open')).toBeVisible();
  });

  test('shows an empty state', async ({ page, mock }) => {
    await openApp(page, mock, '/alerts');
    await expect(page.getByText('No alerts.')).toBeVisible();
  });

  test('acknowledging moves an alert to acknowledged and removes its button', async ({ page, mock }) => {
    await mock.setAlerts([alert({ id: 7, message: 'ack me' })]);
    await openApp(page, mock, '/alerts');
    const row = page.locator('tbody tr', { hasText: 'ack me' });

    await row.getByRole('button', { name: 'Acknowledge' }).click();

    await expect(row.locator('.state-acknowledged')).toBeVisible();
    await expect(row.getByRole('button', { name: 'Acknowledge' })).toHaveCount(0);

    const { requests } = await mock.state();
    const post = requests.find((r) => r.url === '/api/alerts/7/ack');
    expect(post?.body).toEqual({ by: 'dashboard' });
  });

  test('the filter switches between open alerts and the full history', async ({ page, mock }) => {
    await mock.setAlerts([
      alert({ id: 1, message: 'still open' }),
      alert({ id: 2, message: 'long gone', resolvedAt: new Date().toISOString() }),
    ]);
    await openApp(page, mock, '/alerts');

    await expect(page.getByText('still open')).toBeVisible();
    await expect(page.getByText('long gone')).toHaveCount(0);

    await page.getByRole('button', { name: /^All/ }).click();

    const resolved = page.locator('tbody tr', { hasText: 'long gone' });
    await expect(resolved).toBeVisible();
    await expect(resolved).toHaveClass(/row-dim/);
    await expect(resolved.locator('.state-resolved')).toBeVisible();
    await expect(resolved.getByRole('button', { name: 'Acknowledge' })).toHaveCount(0);
  });

  test('a live alert pops a toast and updates the navigation badge', async ({ page, mock }) => {
    await openApp(page, mock);
    const nav = page.getByRole('link', { name: /^Alerts/ });
    await expect(nav.locator('.count')).toHaveCount(0);

    await mock.setAlerts([alert({ id: 3, message: 'Boiler over temperature' })]);
    await mock.emit('Alert', {
      type: 'opened', alertId: 3, severity: 'critical', message: 'Boiler over temperature',
      site: 'site1', line: 'line1', device: 'pump01', sensor: 'temperature', value: 90, time: new Date().toISOString(),
    });

    await expect(page.locator('.toast-alert-opened')).toContainText('Boiler over temperature');
    await expect(nav.locator('.count')).toHaveText('1');
    await expect(page.locator('.stat', { hasText: 'Open alerts' }).locator('.stat-value')).toHaveText('1');
    await expect(page.getByText('Boiler over temperature').first()).toBeVisible();
  });

  test('a resolved event clears the badge', async ({ page, mock }) => {
    await mock.setAlerts([alert({ id: 4, message: 'transient' })]);
    await openApp(page, mock);
    const nav = page.getByRole('link', { name: /^Alerts/ });
    await expect(nav.locator('.count')).toHaveText('1');

    await mock.setAlerts([alert({ id: 4, message: 'transient', resolvedAt: new Date().toISOString() })]);
    await mock.emit('Alert', {
      type: 'resolved', alertId: 4, severity: 'critical', message: 'transient',
      site: 'site1', line: 'line1', device: 'pump01', sensor: 'temperature', value: 90, time: new Date().toISOString(),
    });

    await expect(page.locator('.toast-alert-resolved')).toContainText('Resolved: transient');
    await expect(nav.locator('.count')).toHaveCount(0);
  });

  test('toasts disappear after a few seconds', async ({ page, mock }) => {
    await openApp(page, mock);
    await mock.emit('CommandResult', { site: 'site1', line: 'line1', device: 'pump01', ok: true, error: null, time: new Date().toISOString() });
    await expect(page.locator('.toast')).toBeVisible();
    await expect(page.locator('.toast')).toHaveCount(0, { timeout: 12_000 });
  });
});
