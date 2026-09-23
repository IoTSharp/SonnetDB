import { execFile, spawn } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const port = process.env.SONNETDB_E2E_PORT ?? '4173';
if (!/^\d{1,5}$/u.test(port) || Number(port) < 1 || Number(port) > 65535) {
  throw new Error('SONNETDB_E2E_PORT must be a valid TCP port.');
}
const baseUrl = `http://127.0.0.1:${port}`;
const forwardedArgs = process.argv.slice(2);
const cancellation = new AbortController();
const maximumRun = setTimeout(() => cancellation.abort(new Error('E2E run exceeded 15 minutes.')), 15 * 60_000);
const cancel = () => cancellation.abort(new Error('E2E run cancelled.'));
process.on('SIGINT', cancel);
process.on('SIGTERM', cancel);
const children = [];

// Non-secret .test identity-provider fixtures. These variables belong only to
// the Vite child; normal builds and Node-side contract tests retain their env.
const fixtureEnvironment = {
  VITE_COPILOT_RUNTIME_MODE: 'BrowserDirect',
  VITE_COPILOT_OAUTH_ISSUER: 'https://idp.oauth.test',
  VITE_COPILOT_OAUTH_AUTHORIZATION_ENDPOINT: 'https://idp.oauth.test/authorize',
  VITE_COPILOT_OAUTH_TOKEN_ENDPOINT: 'https://idp.oauth.test/token',
  VITE_COPILOT_OAUTH_CLIENT_ID: 'sonnetdb-browser-e2e',
  VITE_COPILOT_OAUTH_REDIRECT_URI: 'https://studio.oauth.test/admin/copilot/oauth/callback',
  VITE_COPILOT_OAUTH_APPROVED_ORIGINS: 'https://idp.oauth.test',
  VITE_COPILOT_OAUTH_SCOPES: 'openid copilot',
  VITE_COPILOT_BROWSER_DIRECT_PUBLIC_BASE_URL: 'https://ai.oauth.test',
  VITE_COPILOT_BROWSER_DIRECT_APPROVED_ORIGINS: 'https://ai.oauth.test',
};

try {
  if (await isReady()) {
    throw new Error(`E2E port ${port} is already serving HTTP. Set SONNETDB_E2E_PORT to an unused port.`);
  }
  const server = await startChild([
    './node_modules/vite/bin/vite.js', '--mode', 'e2e', '--host', '127.0.0.1',
    '--port', port, '--strictPort',
  ], { ...process.env, ...fixtureEnvironment }, 'ignore');
  await waitUntilReady(server.child);
  const tests = await startChild([
    './node_modules/@playwright/test/cli.js', 'test', ...forwardedArgs,
  ], { ...process.env, SONNETDB_E2E_BASE_URL: baseUrl }, 'inherit');
  process.exitCode = await waitForExit(tests.child, cancellation.signal);
} catch (error) {
  process.exitCode = 1;
  console.error(error instanceof Error ? error.message : String(error));
} finally {
  clearTimeout(maximumRun);
  process.removeListener('SIGINT', cancel);
  process.removeListener('SIGTERM', cancel);
  // At most two owned roots, each cleanup limited to 15 seconds.
  for (const record of children.slice(0, 2).reverse()) {
    try { await stopOwnedTree(record); }
    catch (error) {
      process.exitCode = 1;
      console.error(`Could not verify/clean owned E2E process ${record.pid}: ${String(error)}`);
    }
  }
}

async function startChild(args, environment, stdio) {
  cancellation.signal.throwIfAborted();
  if (children.length >= 2) throw new Error('E2E child-process budget exceeded.');
  const child = spawn(process.execPath, args, {
    cwd: process.cwd(), env: environment, stdio, windowsHide: true,
    detached: process.platform !== 'win32',
  });
  const record = {
    child, pid: child.pid, parentPid: process.pid, startedAtUtc: new Date().toISOString(),
    command: [process.execPath, ...args], identity: null, error: null,
  };
  children.push(record);
  child.on('error', (error) => { record.error = error; });
  await new Promise((resolve, reject) => {
    child.once('spawn', resolve);
    child.once('error', reject);
  });
  if (process.platform === 'win32') record.identity = await windowsIdentity(record.pid);
  console.log(JSON.stringify({ event: 'e2e-process-start', pid: record.pid,
    parentPid: record.parentPid, startedAtUtc: record.startedAtUtc, command: record.command,
    identity: record.identity }));
  return record;
}

async function isReady() {
  cancellation.signal.throwIfAborted();
  try {
    const response = await fetch(baseUrl, {
      signal: AbortSignal.any([cancellation.signal, AbortSignal.timeout(1_000)]),
    });
    await response.body?.cancel();
    return response.ok;
  } catch {
    cancellation.signal.throwIfAborted();
    return false;
  }
}

async function waitUntilReady(child) {
  const deadline = Date.now() + 60_000;
  for (let attempt = 0; attempt < 240 && Date.now() < deadline; attempt++) {
    cancellation.signal.throwIfAborted();
    if (child.exitCode !== null || child.signalCode !== null) {
      throw new Error(`Vite exited before readiness: ${child.exitCode ?? child.signalCode}`);
    }
    if (await isReady()) return;
    if (attempt > 0 && attempt % 40 === 0) console.log(`Waiting for E2E Vite: attempt ${attempt}/240.`);
    await delay(250, undefined, { signal: cancellation.signal });
  }
  throw new Error(`Vite was not ready within 60 seconds/240 attempts at ${baseUrl}.`);
}

function waitForExit(child, signal) {
  if (child.exitCode !== null) return Promise.resolve(child.exitCode);
  if (child.signalCode !== null) return Promise.resolve(1);
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      child.removeListener('exit', exited);
      child.removeListener('error', failed);
      signal.removeEventListener('abort', aborted);
    };
    const exited = (code) => { cleanup(); resolve(code ?? 1); };
    const failed = (error) => { cleanup(); reject(error); };
    const aborted = () => { cleanup(); reject(signal.reason); };
    child.once('exit', exited);
    child.once('error', failed);
    signal.addEventListener('abort', aborted, { once: true });
  });
}

async function windowsIdentity(pid) {
  if (!Number.isSafeInteger(pid) || pid <= 0) throw new Error('Invalid owned process id.');
  const script = `$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$owned = Get-CimInstance Win32_Process -Filter 'ProcessId=${pid}'
if ($null -ne $owned) {
  [ordered]@{ pid = $owned.ProcessId; parentPid = $owned.ParentProcessId;
    created = $owned.CreationDate.ToUniversalTime().ToString('O');
    commandLine = $owned.CommandLine } | ConvertTo-Json -Compress
}`;
  const result = await execFileAsync('C:\\Program Files\\PowerShell\\7\\pwsh.exe', ['-NoProfile', '-Command', script], {
    timeout: 5_000, windowsHide: true, maxBuffer: 32 * 1024,
  });
  return result.stdout.trim() ? JSON.parse(result.stdout) : null;
}

async function stopOwnedTree(record) {
  if (!record.pid) return;
  if (process.platform === 'win32') {
    const current = await windowsIdentity(record.pid);
    if (!current) return;
    const expected = record.identity;
    if (!expected || current.created !== expected.created || current.parentPid !== process.pid
      || current.commandLine !== expected.commandLine || current.pid !== expected.pid) {
      throw new Error('PID/creation time/parent/command ownership changed; process preserved.');
    }
    await execFileAsync('taskkill.exe', ['/PID', String(record.pid), '/T', '/F'], {
      timeout: 10_000, windowsHide: true, maxBuffer: 64 * 1024,
    });
  } else {
    // Each root was spawned as its own process group, so this affects only its
    // descendants (including Vite helpers or Playwright browsers).
    try { process.kill(-record.pid, 'SIGKILL'); }
    catch (error) { if (error?.code !== 'ESRCH') throw error; }
  }
  console.log(JSON.stringify({ event: 'e2e-process-cleaned', pid: record.pid, command: record.command }));
}
