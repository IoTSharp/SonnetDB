import { spawnSync } from 'node:child_process';

const result = spawnSync(
  process.execPath,
  [
    './e2e/run-playwright.mjs',
    '--grep',
    'workbench-mq|Studio bridge',
  ],
  {
    cwd: process.cwd(),
    env: { ...process.env, SONNETDB_CAPTURE_DOCS: '1' },
    stdio: 'inherit',
  },
);

process.exit(result.status ?? 1);
