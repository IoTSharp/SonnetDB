import { defineConfig } from '@playwright/test';
import config from './playwright.config';

// This suite uses the Vite/API fixtures. Real permission journeys still use
// playwright.config.ts through their isolated run-*-real.mjs entry points;
// their required Server URL/evidence checks must not become skips or mocks.
export default defineConfig(config, {
  testIgnore: '**/*-real*.spec.ts',
});
