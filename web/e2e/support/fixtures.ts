import { test as base } from '@playwright/test';
import { Mock } from './mock';

/** `mock` is reset to its seed data before every test. */
export const test = base.extend<{ mock: Mock }>({
  mock: async ({ request }, use) => {
    const mock = new Mock(request);
    await mock.reset();
    await use(mock);
  },
});

export { expect } from '@playwright/test';
