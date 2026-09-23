import { defineConfig, devices } from '@playwright/test';

const baseURL = process.env.SONNETDB_E2E_BASE_URL ?? 'http://127.0.0.1:4173';

export default defineConfig({
  testDir: './e2e',
  outputDir: '../artifacts/playwright/management-workbench',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  workers: 1,
  reporter: process.env.CI ? [['list'], ['html', { outputFolder: '../artifacts/playwright/report', open: 'never' }]] : 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: process.env.SONNETDB_E2E_VIDEO === 'off' ? 'off' : 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1600, height: 1000 },
        ...(process.env.SONNETDB_E2E_EXECUTABLE_PATH
          ? { launchOptions: { executablePath: process.env.SONNETDB_E2E_EXECUTABLE_PATH } }
          : {}),
      },
    },
  ],
});
