import { expect, type APIRequestContext, type Page } from '@playwright/test';

const MOCK = 'http://localhost:5081';

/** Typed access to the mock backend's control endpoints. */
export class Mock {
  constructor(private readonly api: APIRequestContext) {}

  private async post(path: string, data?: unknown) {
    const res = await this.api.post(`${MOCK}${path}`, { data });
    expect(res.ok(), `${path} -> ${res.status()}`).toBeTruthy();
  }

  reset() { return this.post('/__mock/reset'); }
  emit(target: string, ...args: unknown[]) { return this.post('/__mock/emit', { target, args }); }
  setDevices(devices: unknown[]) { return this.post('/__mock/devices', devices); }
  setAlerts(alerts: unknown[]) { return this.post('/__mock/alerts', alerts); }
  /** The next fault command gets this reply instead of the default one. */
  nextReply(reply: { ok: boolean; error?: string }) { return this.post('/__mock/next-reply', reply); }
  replyDelay(ms: number) { return this.post('/__mock/reply-delay', { ms }); }
  dropConnections() { return this.post('/__mock/drop'); }

  async state(): Promise<{
    sockets: number;
    negotiates: number;
    requests: { method: string; url: string; body?: any }[];
  }> {
    return (await this.api.get(`${MOCK}/__mock/state`)).json();
  }

  /** Waits until the page's hub connection is registered on the mock, so emitted events are not lost. */
  async waitForHub() {
    await expect.poll(async () => (await this.state()).sockets, { message: 'a hub client to connect' }).toBeGreaterThan(0);
  }
}

export function reading(over: Record<string, unknown> = {}) {
  return {
    site: 'site1', line: 'line1', device: 'pump01', sensor: 'temperature',
    value: 71.5, unit: 'C', time: new Date().toISOString(), ...over,
  };
}

export function alert(over: Record<string, unknown> = {}) {
  return {
    id: 1, ruleId: 1, seriesId: 1, site: 'site1', line: 'line1', device: 'pump01', sensor: 'temperature',
    severity: 'critical', message: 'High temperature: temperature 90 > 80 [pump01]', value: 90,
    openedAt: new Date(Date.now() - 30_000).toISOString(), resolvedAt: null, acknowledgedAt: null, acknowledgedBy: null,
    ...over,
  };
}

/** Opens the app and waits for the live connection, so the test can start emitting events. */
export async function openApp(page: Page, mock: Mock, path = '/') {
  await page.goto(path);
  await mock.waitForHub();
  await expect(page.locator('.conn')).toHaveText('Live');
}
