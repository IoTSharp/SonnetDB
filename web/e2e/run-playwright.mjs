import { spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';

const baseUrl = 'http://127.0.0.1:4173';
const forwardedArgs = process.argv.slice(2);
let server = null;

try {
  if (!await isReady()) {
    server = spawn(
      process.execPath,
      [
        './node_modules/vite/bin/vite.js',
        '--mode',
        'e2e',
        '--host',
        '127.0.0.1',
        '--port',
        '4173',
      ],
      {
        cwd: process.cwd(),
        stdio: 'ignore',
        windowsHide: true,
      },
    );
    await waitUntilReady(server);
  }

  process.exitCode = await run(
    process.execPath,
    ['./node_modules/@playwright/test/cli.js', 'test', ...forwardedArgs],
  );
}
finally {
  if (server && server.exitCode === null) {
    server.kill();
    await Promise.race([
      new Promise((resolve) => server.once('exit', resolve)),
      delay(5_000),
    ]);
  }
}

async function isReady() {
  try {
    const response = await fetch(baseUrl);
    return response.ok;
  }
  catch {
    return false;
  }
}

async function waitUntilReady(processHandle) {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    if (processHandle.exitCode !== null) {
      throw new Error(`Vite e2e server exited with code ${processHandle.exitCode}.`);
    }
    if (await isReady()) return;
    await delay(200);
  }
  throw new Error(`Vite e2e server did not become ready at ${baseUrl}.`);
}

function run(command, args) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: process.cwd(),
      env: process.env,
      stdio: 'inherit',
      windowsHide: true,
    });
    child.once('error', reject);
    child.once('exit', (code) => resolve(code ?? 1));
  });
}
