import { execFile, spawn } from 'node:child_process';
import { createWriteStream } from 'node:fs';
import { access, appendFile, lstat, mkdir, readFile, realpath, rm, writeFile } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(webRoot, '..');
const serverDll = path.join(repositoryRoot, 'src', 'SonnetDB', 'bin', 'Release', 'net10.0', 'SonnetDB.dll');
const powershell = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
const dotnet = process.platform === 'win32' ? 'C:\\Program Files\\dotnet\\dotnet.exe' : 'dotnet';
const configuredEvidenceRoot = required('SONNETDB_DOCUMENT_REAL_EVIDENCE_ROOT');
if (!path.isAbsolute(configuredEvidenceRoot)) throw new Error('SONNETDB_DOCUMENT_REAL_EVIDENCE_ROOT must be an absolute path.');
const evidenceRoot = await realpath(configuredEvidenceRoot);
if (!(await lstat(evidenceRoot)).isDirectory()) throw new Error('The evidence root must already exist as a directory.');
const serverPort = port('SONNETDB_DOCUMENT_REAL_SERVER_PORT');
const vitePort = port('SONNETDB_E2E_PORT');
const browserExecutable = required('SONNETDB_E2E_EXECUTABLE_PATH');
if (!path.isAbsolute(browserExecutable) || path.basename(browserExecutable).toLowerCase() !== 'chrome.exe') {
  throw new Error('SONNETDB_E2E_EXECUTABLE_PATH must name the explicitly configured absolute Chrome executable.');
}
if (serverPort === vitePort) throw new Error('Server and Vite must use different ports.');
const baseUrl = `http://127.0.0.1:${serverPort}`;
const runId = `document-real-${new Date().toISOString().replace(/[:.]/gu, '-')}-${randomUUID()}`;
const runRoot = path.join(evidenceRoot, runId);
const contentRoot = path.join(runRoot, 'server-content');
const dataRoot = path.join(contentRoot, 'data');
const identityLog = path.join(runRoot, 'process-identities.jsonl');
const cancellation = new AbortController();
const deadline = Date.now() + 10 * 60_000;
const roots = [];
const identities = new Map();
const logStreams = [];
let auditCount = 0;
let auditPending = null;
let auditTimer = null;
let cleanupProven = true;
let contentCreated = false;
const cancel = () => cancellation.abort(new Error('Document real Server run cancelled.'));
const maximumRun = setTimeout(() => cancellation.abort(new Error('Document real Server run exceeded 10 minutes.')), 10 * 60_000);
process.on('SIGINT', cancel);
process.on('SIGTERM', cancel);

await mkdir(runRoot);
await writeFile(path.join(runRoot, 'run.json'), JSON.stringify({ runId, baseUrl, serverDll, contentRoot, dataRoot,
  runnerPid: process.pid, startedAtUtc: new Date().toISOString(), maximumMinutes: 10, test: 'document-real-permission.spec.ts' }, null, 2));

try {
  await access(serverDll);
  await access(browserExecutable);
  await access(path.join(webRoot, 'node_modules', 'vite', 'bin', 'vite.js'));
  await access(path.join(webRoot, 'node_modules', '@playwright', 'test', 'cli.js'));
  await access(path.join(webRoot, 'e2e', 'document-real-permission.spec.ts'));
  if (await responds(baseUrl) || await responds(`http://127.0.0.1:${vitePort}`)) {
    throw new Error('A required loopback port is already serving HTTP; existing listeners are preserved.');
  }
  await mkdir(contentRoot);
  contentCreated = true;
  await writeFile(path.join(contentRoot, '.document-real-owner.json'), JSON.stringify({ runId, runnerPid: process.pid, contentRoot, dataRoot }));
  await writeFile(path.join(contentRoot, 'appsettings.json'), JSON.stringify({
    Logging: { LogLevel: { Default: 'Information', 'Microsoft.AspNetCore': 'Warning' } },
    AllowedHosts: '127.0.0.1',
    Kestrel: { Endpoints: { Http: { Url: baseUrl, Protocols: 'Http1' } } },
    SonnetDBServer: {
      DataRoot: dataRoot, AutoLoadExistingDatabases: true, AllowAnonymousProbes: true, Tokens: {},
      Mqtt: { Enabled: false, Sparkplug: { Enabled: false }, ExternalClient: { Enabled: false } },
      Coap: { Enabled: false, Dtls: { Enabled: false } },
      LineProtocolUdp: { Enabled: false }, Modbus: { Enabled: false }, SemanticSearch: { Enabled: false },
    },
  }, null, 2));
  const environment = isolatedEnvironment();
  const server = await startRoot(dotnet, [serverDll, '--contentRoot', contentRoot, '--environment', 'Production'],
    { ...environment, DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production' }, 'server');
  await auditOwnedProcesses();
  auditTimer = setInterval(() => {
    if (auditPending || cancellation.signal.aborted) return;
    if (auditCount >= 300 || Date.now() >= deadline) { cancel(); return; }
    auditPending = auditOwnedProcesses().catch((error) => {
      cleanupProven = false; cancellation.abort(error);
    }).finally(() => { auditPending = null; });
  }, 2_000);
  await waitUntilReady(server.child);
  const tests = await startRoot(process.execPath, [path.join(webRoot, 'e2e', 'run-playwright.mjs'),
    'e2e/document-real-permission.spec.ts', '--project=chromium', '--workers=1', '--retries=0',
    '--reporter=list', '--output', path.join(runRoot, 'playwright-output')], {
    ...environment, SONNETDB_E2E_PORT: vitePort, SONNETDB_DOCUMENT_REAL_BASE_URL: baseUrl,
    SONNETDB_PROXY_TARGET: baseUrl, SONNETDB_DOCUMENT_REAL_EVIDENCE_ROOT: runRoot,
    SONNETDB_E2E_EXECUTABLE_PATH: browserExecutable, SONNETDB_E2E_VIDEO: 'off',
    SONNETDB_WEB_BASE_PATH: '/', SONNETDB_E2E_RUNTIME: 'BrowserDirect',
  }, 'playwright');
  process.exitCode = await waitForExit(tests.child, cancellation.signal);
} catch (error) {
  process.exitCode = 1;
  console.error(error instanceof Error ? error.message : String(error));
  await event({ event: 'document-real-failed', message: error instanceof Error ? error.message : String(error) });
} finally {
  clearTimeout(maximumRun);
  clearInterval(auditTimer);
  process.removeListener('SIGINT', cancel);
  process.removeListener('SIGTERM', cancel);
  if (auditPending) await auditPending;
  try { await auditOwnedProcesses(true); }
  catch (error) { cleanupProven = false; console.error(`Final process discovery failed: ${String(error)}`); }
  // Two owned long-lived roots. Each stop verifies the OS identity and has a 10-second timeout.
  for (const record of roots.slice(0, 2).reverse()) {
    try { await stopOwnedTree(record.identity); }
    catch (error) { cleanupProven = false; console.error(`Owned root cleanup failed: ${String(error)}`); }
  }
  // A root may already have exited. Recheck only recorded descendants, never processes selected by name.
  try {
    const snapshot = await windowsSnapshot();
    const remaining = liveOwned(snapshot).reverse();
    if (remaining.length > 128) throw new Error('Owned descendant cleanup budget exceeded.');
    const cleanupDeadline = Date.now() + 30_000;
    for (let index = 0; index < remaining.length && index < 128; index += 1) {
      if (Date.now() >= cleanupDeadline) throw new Error('Owned descendant cleanup exceeded 30 seconds.');
      await stopOwnedTree(remaining[index]);
    }
    const finalSnapshot = await windowsSnapshot();
    const live = liveOwned(finalSnapshot);
    if (live.length !== 0) cleanupProven = false;
    await event({ event: 'document-real-process-audit', liveOwned: live, cleanupProven, identities: identities.size });
  } catch (error) { cleanupProven = false; console.error(`Process audit failed: ${String(error)}`); }
  await Promise.all(logStreams.map((stream) => new Promise((resolve) => stream.end(resolve))));
  if (contentCreated && cleanupProven) {
    try {
      await removeOwnedContent();
      await writeFile(path.join(runRoot, 'cleanup-status.json'), JSON.stringify({ cleanupProven: true, contentRemoved: true, contentRoot }, null, 2));
    } catch (error) {
      process.exitCode = 1;
      await writeFile(path.join(runRoot, 'cleanup-status.json'), JSON.stringify({ cleanupProven: true, contentRemoved: false, contentRoot, error: String(error) }, null, 2));
      console.error(`Owned content cleanup rejected/failed; directory preserved: ${contentRoot}. ${String(error)}`);
    }
  } else {
    if (!cleanupProven) process.exitCode = 1;
    await writeFile(path.join(runRoot, 'cleanup-status.json'), JSON.stringify({ cleanupProven, contentRemoved: !contentCreated, contentRoot,
      note: 'Unproven process cleanup preserves content; use the root-owned process audit before any further cleanup.' }, null, 2));
  }
  console.log(`Document real Server evidence: ${runRoot}`);
}

function required(name) {
  const value = process.env[name];
  if (!value || !value.trim()) throw new Error(`${name} is required; this runner never skips real Server validation.`);
  return value;
}

function port(name) {
  const value = required(name);
  if (!/^\d{1,5}$/u.test(value) || Number(value) < 1 || Number(value) > 65535) throw new Error(`${name} must be a valid TCP port.`);
  return value;
}

function isolatedEnvironment() {
  const environment = { ...process.env };
  const names = Object.keys(environment);
  if (names.length > 2048) throw new Error('Environment-variable budget exceeded.');
  const expires = Date.now() + 1_000;
  for (let index = 0; index < names.length; index += 1) {
    if (Date.now() >= expires) throw new Error('Environment filtering exceeded one second.');
    if (/^(SONNETDB_|ASPNETCORE_|DOTNET_ENVIRONMENT$|SonnetDBServer(?:__|:)|Kestrel(?:__|:)|ConnectionStrings(?:__|:)|URLS$)/iu.test(names[index])) delete environment[names[index]];
  }
  return environment;
}

async function event(value) {
  const line = JSON.stringify({ timestampUtc: new Date().toISOString(), ...value });
  await appendFile(identityLog, `${line}\n`);
  console.log(line);
}

async function startRoot(executable, args, environment, label) {
  cancellation.signal.throwIfAborted();
  if (roots.length >= 2) throw new Error('Document real Server root-process budget exceeded.');
  const child = spawn(executable, args, { cwd: webRoot, env: environment, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  const record = { child, pid: child.pid, parentPid: process.pid, command: [executable, ...args],
    startedAtUtc: new Date().toISOString(), identity: null, exitedAtUtc: null };
  roots.push(record);
  child.once('exit', () => { record.exitedAtUtc = new Date().toISOString(); });
  capture(child.stdout, path.join(runRoot, `${label}.stdout.log`), label);
  capture(child.stderr, path.join(runRoot, `${label}.stderr.log`), label);
  await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
  if (process.platform !== 'win32') throw new Error('This isolated runner requires the verified Windows PowerShell 7 process ownership path.');
  const snapshot = await windowsSnapshot();
  const identity = snapshot.find((item) => item.pid === record.pid);
  if (!identity || identity.parentPid !== process.pid || !identity.commandLine) {
    cleanupProven = false; throw new Error('Could not capture exact owned root identity; root process audit is required.');
  }
  identity.parentChain = parentChain(identity, snapshot);
  record.identity = identity;
  identities.set(identity.pid, identity);
  await event({ event: 'document-real-process-start', label, ...record.identity, command: record.command });
  return record;
}

function capture(source, destination, label) {
  const stream = createWriteStream(destination, { flags: 'wx' });
  logStreams.push(stream);
  let bytes = 0;
  const maximumBytes = 4 * 1024 * 1024;
  source.on('data', (chunk) => {
    if (bytes < maximumBytes) {
      const kept = chunk.subarray(0, maximumBytes - bytes);
      bytes += kept.length; stream.write(kept);
      if (label === 'playwright') process.stdout.write(kept);
    }
  });
  stream.on('error', (error) => cancellation.abort(error));
}

async function responds(url, requireSuccess = false) {
  cancellation.signal.throwIfAborted();
  try {
    const response = await fetch(url, { signal: AbortSignal.any([cancellation.signal, AbortSignal.timeout(1_000)]) });
    await response.body?.cancel();
    return !requireSuccess || response.ok;
  } catch { cancellation.signal.throwIfAborted(); return false; }
}

async function waitUntilReady(child) {
  const expires = Date.now() + 60_000;
  for (let attempt = 0; attempt < 120 && Date.now() < expires; attempt += 1) {
    cancellation.signal.throwIfAborted();
    if (child.exitCode !== null || child.signalCode !== null) throw new Error(`Server exited before readiness: ${child.exitCode ?? child.signalCode}`);
    if (await responds(`${baseUrl}/healthz/ready`, true)) return;
    if (attempt > 0 && attempt % 20 === 0) console.log(`Waiting for real Kestrel: ${attempt}/120.`);
    await delay(500, undefined, { signal: cancellation.signal });
  }
  throw new Error('Real Kestrel was not ready within 60 seconds/120 attempts.');
}

function waitForExit(child, signal) {
  if (child.exitCode !== null) return Promise.resolve(child.exitCode);
  if (child.signalCode !== null) return Promise.resolve(1);
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const cleanup = () => { child.removeListener('exit', exited); child.removeListener('error', failed); signal.removeEventListener('abort', aborted); };
    const exited = (code) => { cleanup(); resolve(code ?? 1); };
    const failed = (error) => { cleanup(); reject(error); };
    const aborted = () => { cleanup(); reject(signal.reason); };
    child.once('exit', exited); child.once('error', failed); signal.addEventListener('abort', aborted, { once: true });
  });
}

async function windowsSnapshot() {
  if (process.platform !== 'win32') throw new Error('PowerShell 7 Windows process auditing is required.');
  const script = `$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$taskProcesses = @(Get-CimInstance Win32_Process | Select-Object -First 4097)
if ($taskProcesses.Count -gt 4096) { throw 'Process snapshot budget exceeded.' }
$taskDeadline = [DateTime]::UtcNow.AddSeconds(3)
$taskRecords = [Collections.Generic.List[object]]::new()
for ($taskIndex = 0; $taskIndex -lt $taskProcesses.Count; $taskIndex++) {
  if ([DateTime]::UtcNow -ge $taskDeadline) { throw 'Process snapshot deadline exceeded.' }
  $taskProcess = $taskProcesses[$taskIndex]
  if ($null -eq $taskProcess.CreationDate) { continue }
  $taskRecords.Add([ordered]@{ pid = [int]$taskProcess.ProcessId; parentPid = [int]$taskProcess.ParentProcessId;
    created = $taskProcess.CreationDate.ToUniversalTime().ToString('O'); commandLine = $taskProcess.CommandLine })
}
ConvertTo-Json -InputObject ($taskRecords.ToArray()) -Compress -Depth 4`;
  const result = await execFileAsync(powershell, ['-NoProfile', '-Command', script], { timeout: 5_000, windowsHide: true, maxBuffer: 4 * 1024 * 1024 });
  const snapshot = JSON.parse(result.stdout);
  if (!Array.isArray(snapshot) || snapshot.length > 4096) throw new Error('Invalid bounded process snapshot.');
  return snapshot;
}

function sameIdentity(left, right) {
  return left && right && left.pid === right.pid && left.created === right.created
    && left.parentPid === right.parentPid && left.commandLine === right.commandLine;
}

function parentChain(identity, snapshot) {
  const chain = [];
  let parentPid = identity.parentPid;
  const expires = Date.now() + 1_000;
  for (let depth = 0; depth < 16 && parentPid > 0 && Date.now() < expires; depth += 1) {
    const parent = identities.get(parentPid) ?? snapshot.find((item) => item.pid === parentPid);
    if (!parent) { chain.push({ pid: parentPid, unavailable: true }); break; }
    chain.push({ pid: parent.pid, created: parent.created, parentPid: parent.parentPid, commandLine: parent.commandLine });
    if (chain.slice(0, -1).some((item) => item.pid === parent.parentPid)) break;
    parentPid = parent.parentPid;
  }
  return chain;
}

async function auditOwnedProcesses(final = false) {
  if (!final && (auditCount >= 300 || Date.now() >= deadline)) throw new Error('Owned process discovery budget exceeded.');
  auditCount += 1;
  const snapshot = await windowsSnapshot();
  const expires = Date.now() + 3_000;
  for (let depth = 0; depth < 16 && Date.now() < expires; depth += 1) {
    let added = 0;
    for (let index = 0; index < snapshot.length && index < 4096; index += 1) {
      if (Date.now() >= expires) throw new Error('Owned process traversal exceeded three seconds.');
      const child = snapshot[index];
      const parent = identities.get(child.parentPid);
      if (!parent || identities.has(child.pid) || Date.parse(child.created) < Date.parse(parent.created)) continue;
      const currentParent = snapshot.find((item) => item.pid === parent.pid);
      const root = roots.find((item) => item.pid === parent.pid);
      if (currentParent && !sameIdentity(currentParent, parent)) continue;
      if (!currentParent && (!root?.exitedAtUtc || Date.parse(child.created) > Date.parse(root.exitedAtUtc))) continue;
      if (identities.size >= 128) throw new Error('Owned process identity budget exceeded.');
      child.parentChain = parentChain(child, snapshot);
      identities.set(child.pid, child); added += 1;
      await event({ event: 'document-real-descendant-discovered', ...child });
    }
    if (added === 0) return;
  }
  throw new Error('Owned process tree exceeded depth/time budget.');
}

function liveOwned(snapshot) {
  const live = [];
  for (const identity of [...identities.values()].slice(0, 128)) {
    const current = snapshot.find((item) => item.pid === identity.pid);
    if (current && !sameIdentity(current, identity)) throw new Error(`Recorded PID ${identity.pid} was reused; preserve the replacement process.`);
    if (current) live.push(identity);
  }
  return live;
}

async function stopOwnedTree(identity) {
  if (!identity) { cleanupProven = false; throw new Error('Missing process identity; cleanup must be reviewed by the root.'); }
  const current = (await windowsSnapshot()).find((item) => item.pid === identity.pid);
  if (!current) return;
  if (!sameIdentity(current, identity) || !identity.parentChain?.length) throw new Error('PID/creation/command/parent-chain ownership changed; process preserved.');
  await execFileAsync('C:\\Windows\\System32\\taskkill.exe', ['/PID', String(identity.pid), '/T', '/F'], {
    timeout: 10_000, windowsHide: true, maxBuffer: 64 * 1024,
  });
  await event({ event: 'document-real-process-stopped', ...identity });
}

async function removeOwnedContent() {
  const resolved = await realpath(contentRoot);
  const expected = path.resolve(runRoot, 'server-content');
  const comparison = (value) => process.platform === 'win32' ? value.toLowerCase() : value;
  if (!path.isAbsolute(resolved) || comparison(resolved) !== comparison(expected)
    || path.dirname(expected) !== runRoot || (await lstat(contentRoot)).isSymbolicLink()) throw new Error('Owned content absolute-path verification failed.');
  const marker = JSON.parse(await readFile(path.join(contentRoot, '.document-real-owner.json'), 'utf8'));
  if (marker.runId !== runId || marker.runnerPid !== process.pid || marker.contentRoot !== contentRoot || marker.dataRoot !== dataRoot) throw new Error('Owned content marker mismatch.');
  await rm(resolved, { recursive: true, force: false, maxRetries: 0 });
}
