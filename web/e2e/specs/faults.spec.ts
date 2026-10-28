import { test, expect } from '../support/fixtures';
import { openApp } from '../support/mock';

const DEVICE = '/devices/site1/line1/pump01';

const panel = (page: import('@playwright/test').Page) =>
  page.locator('section.card', { has: page.getByRole('heading', { name: 'Inject a fault' }) });

test.describe('fault injection', () => {
  test('sends the chosen command and reports the device\'s acceptance', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    const form = panel(page);

    await form.getByLabel('Sensor').selectOption('pressure');
    await form.getByLabel('Fault').selectOption('ramp');
    await form.getByLabel('Duration (samples)').fill('120');
    await form.getByRole('button', { name: 'Inject' }).click();

    await expect(form.getByText('The device accepted the command.')).toBeVisible();

    const { requests } = await mock.state();
    const post = requests.find((r) => r.method === 'POST' && r.url === '/api/devices/site1/line1/pump01/faults');
    expect(post?.body).toEqual({ sensor: 'pressure', fault: 'ramp', duration: 120 });
  });

  test('leaves the duration out when the field is empty, so the device uses its default', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    await panel(page).getByRole('button', { name: 'Inject' }).click();
    await expect(panel(page).getByText('The device accepted the command.')).toBeVisible();

    const { requests } = await mock.state();
    const post = requests.find((r) => r.method === 'POST' && r.url.endsWith('/faults'));
    expect(post?.body).toEqual({ sensor: 'temperature', fault: 'spike' });
    expect(post?.body).not.toHaveProperty('duration');
  });

  test('shows the reason when the device rejects the command', async ({ page, mock }) => {
    await mock.nextReply({ ok: false, error: 'device has no sensor "vibration"' });
    await openApp(page, mock, DEVICE);

    await panel(page).getByRole('button', { name: 'Inject' }).click();

    await expect(panel(page).getByText('The device rejected it: device has no sensor "vibration"')).toBeVisible();
  });

  test('does not mistake an older reply for the answer to the new command', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    const form = panel(page);

    // An earlier, unrelated rejection is already sitting in the store...
    await mock.emit('CommandResult', { site: 'site1', line: 'line1', device: 'pump01', ok: false, error: 'old failure', time: new Date().toISOString() });
    await expect(form.getByText('old failure')).toHaveCount(0); // not shown before sending anything

    // ...and the new command's answer is slow.
    await mock.replyDelay(1500);
    await form.getByRole('button', { name: 'Inject' }).click();

    await expect(form.getByText('Command sent. Waiting for the device to reply…')).toBeVisible();
    await expect(form.getByText('old failure')).toHaveCount(0);
    await expect(form.getByText('The device accepted the command.')).toBeVisible();
  });

  test('rejects an invalid duration in the form without calling the API', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    const form = panel(page);
    const duration = form.getByLabel('Duration (samples)');
    const submit = form.getByRole('button', { name: 'Inject' });

    for (const bad of ['0', '-3', '1.5', 'abc', '1000001']) {
      await duration.fill(bad);
      await expect(form.getByText('Duration must be a whole number')).toBeVisible();
      await expect(submit).toBeDisabled();
    }
    await duration.fill('50');
    await expect(form.getByText('Duration must be a whole number')).toHaveCount(0);
    await expect(submit).toBeEnabled();

    const { requests } = await mock.state();
    expect(requests.filter((r) => r.url.endsWith('/faults'))).toHaveLength(0);
  });

  test('shows the API\'s error when the command cannot be published', async ({ page, mock }) => {
    await page.route('**/faults', (route) =>
      route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ detail: 'MQTT broker not connected' }) }),
    );
    await openApp(page, mock, DEVICE);

    await panel(page).getByRole('button', { name: 'Inject' }).click();

    await expect(panel(page).getByRole('alert')).toContainText('MQTT broker not connected');
    await expect(panel(page).getByText('The device accepted the command.')).toHaveCount(0);
  });

  test('warns when the device is offline', async ({ page, mock }) => {
    await openApp(page, mock, '/devices/site1/line2/motor02');
    await expect(panel(page).getByText('This device is offline')).toBeVisible();
  });

  test('only offers the sensors the device actually has', async ({ page, mock }) => {
    await openApp(page, mock, DEVICE);
    const options = await panel(page).getByLabel('Sensor').locator('option').allTextContents();
    expect(options).toEqual(['temperature', 'pressure']);
  });
});
