import { defineConfig, devices } from '@playwright/test';

// Hermetic suite: a mock backend (e2e/mock) plus the Vite dev server proxied to it.
// No Docker, no database, no broker. Runs the same on a laptop and in CI.
const MOCK_PORT = 5081;
const WEB_PORT = 5174;

export default defineConfig({
  testDir: './e2e/specs',
  fullyParallel: false, // one shared mock backend, so tests run one after another
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  timeout: 30_000,
  expect: { timeout: 7_000 },
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: {
      // Set CHROMIUM_PATH when the browser Playwright expects is not the one installed.
      executablePath: process.env.CHROMIUM_PATH || undefined,
    },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
  webServer: [
    {
      command: 'npx tsx e2e/mock/server.ts',
      url: `http://localhost:${MOCK_PORT}/healthz`,
      env: { PORT: String(MOCK_PORT) },
      reuseExistingServer: !process.env.CI,
    },
    {
      command: `npx vite --port ${WEB_PORT} --strictPort`,
      url: `http://localhost:${WEB_PORT}`,
      env: { VITE_PROXY_TARGET: `http://localhost:${MOCK_PORT}` },
      reuseExistingServer: !process.env.CI,
    },
  ],
});
