import { defineConfig, devices } from '@playwright/test';

// Smoke tests against the REAL stack: simulators, broker, database, ingestion, API.
//
//   docker compose up -d --build            (from the repo root; at least sim-pump01)
//   npm run dev                             (proxies to the API; set VITE_PROXY_TARGET if it is not :5080)
//   npm run e2e:stack
//
// STACK_URL is where the dashboard is served (Vite dev server or the nginx container).
// STACK_DEVICE is a simulated device with temperature and pressure sensors (compose: pump01).
export default defineConfig({
  testDir: './e2e/stack',
  workers: 1,
  timeout: 60_000,
  expect: { timeout: 20_000 }, // real simulators publish once per second
  reporter: [['list']],
  use: {
    baseURL: process.env.STACK_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: { executablePath: process.env.CHROMIUM_PATH || undefined },
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
