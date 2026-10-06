// WB-40: one actual NativeWebApp/WebView2 window, actual native bootstrap and
// isolated Studio-owned Server. No browser launch, routes, nativeWeb injection,
// custom events, private Vue APIs, or simulated host contracts are used here.
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { access, lstat, mkdir, readFile, readdir, realpath, rmdir, stat, unlink, writeFile } from 'node:fs/promises';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';
import { chromium } from '@playwright/test';
import { compactNativeProcessEvidence, encodeNativeEvidence, nativeIdentityKey, persistNativeTerminalEvidence } from './studio-native-evidence.mjs';

const repository = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const scenario = process.env.SONNETDB_STUDIO_NATIVE_REAL_SCENARIO ?? 'lifecycle';
const sqlDialogsRequested = scenario === 'sql-dialogs';
const validationSlice = sqlDialogsRequested ? 'WB-41' : 'WB-40';
const evidenceParents = Object.freeze({
  wb39: path.join(repository, 'artifacts', 'wb39-validation-20261007'),
  wb40: path.join(repository, 'artifacts', 'wb40-validation-20261007'),
  wb41: path.join(repository, 'artifacts', 'wb41-validation-20261007'),
});
const configuredEvidence = process.env.SONNETDB_STUDIO_NATIVE_REAL_EVIDENCE_ROOT;
const selectedEvidence = configuredEvidence && path.isAbsolute(configuredEvidence)
  ? Object.entries(evidenceParents).find(([, value]) => path.resolve(configuredEvidence).toLowerCase() === value.toLowerCase()) : undefined;
const evidenceParent = selectedEvidence?.[1] ?? evidenceParents.wb40;
const windowMode = process.env.SONNETDB_STUDIO_NATIVE_REAL_WINDOW_MODE ?? 'default';
const windowConfiguration = Object.freeze({
  mode: windowMode,
  dimensionsOmitted: windowMode === 'default',
  requestedWidth: windowMode === 'narrow' ? 1000 : null,
  requestedHeight: windowMode === 'narrow' ? 800 : null,
  dimensionArguments: windowMode === 'narrow' ? ['--width', '1000', '--height', '800'] : [],
  programDefaultsWhenOmitted: { width: 1440, height: 920 },
  requiredNarrowCssMaximumWidth: windowMode === 'narrow' ? 1100 : null,
});
const pwsh = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
const helper = path.join(repository, 'web', 'e2e', 'studio-native-process.ps1');
const studioExe = path.join(repository, 'src', 'SonnetDB.Studio', 'bin', 'Release', 'net10.0-windows', 'SonnetDB.Studio.exe');
const studioDll = path.join(path.dirname(studioExe), 'SonnetDB.Studio.dll');
const serverDll = path.join(repository, 'src', 'SonnetDB', 'bin', 'Release', 'net10.0', 'SonnetDB.dll');
const serverWebRoot = path.join(path.dirname(serverDll), 'wwwroot');
const ports = Object.freeze({ http: 18338, frame: 18339, bridge: 55338, cdp: 9338 });
const origin = `http://127.0.0.1:${ports.http}`;
const bridgeOrigin = `http://127.0.0.1:${ports.bridge}`;
const cdpOrigin = `http://127.0.0.1:${ports.cdp}`;
const runId = `studio-native-real-${randomUUID()}`;
const runRoot = path.join(evidenceParent, runId);
const profileRoot = path.join(runRoot, 'profile');
const dataRoot = path.join(runRoot, 'data');
const contentRoot = path.join(runRoot, 'server-content');
const libraryPath = path.join(contentRoot, 'connections.json');
const markerName = '.wb39-owned.json';
const startedAt = Date.now();
const totalDeadline = startedAt + 600_000;
const mainDeadline = totalDeadline - 60_000;
const cleanupDeadline = totalDeadline - 15_000;
const cancellation = new AbortController();
const cancel = () => cancellation.abort(new Error('Native Studio validation cancelled.'));
process.once('SIGINT', cancel);
process.once('SIGTERM', cancel);
const timeout = setTimeout(cancel, mainDeadline - Date.now());
const records = [];
const helpers = [];
const owned = new Map();
const bridgeEvidence = [];
const nativeWindowObservations = [];
const nativeUiPreparation = { kind: 'ordinary Explorer collapse', maximumClicks: 1, needed: false, clicked: false, collapsedObserved: false };
const sqlDialogFixture = "SELECT 'WB41 原生对话框' AS DialogEvidence; -- not executed";
const sqlDialogPaths = Object.freeze({ input: path.join(runRoot, 'wb41-input.sql'), output: path.join(runRoot, 'wb41-saved.sql') });
const sqlDialogPhases = [];
let activeSqlDialog;
let savedSqlBytes;
const counters = { helpers: 0, requests: 0, bridgeResponses: 0, pageRequests: 0, filesWritten: 0, fileWriteAttempts: 0, pageErrors: 0 };
const secretValues = new Set();
let studio;
let studioIdentity;
let runnerIdentity;
let browser;
let page;
let stage = 'prerequisites';
let normalExit = false;
let cleanupProven = false;
let fatal;
let asynchronousFailure;
const streamCounts = { stdoutBytes: 0, stderrBytes: 0 };
const maxOwnedProcesses = 16;
const nativeClose = { attempted: false, accepted: false, method: 'CloseMainWindow', discovery: null,
  studioIdentityExited: false, oldServerIdentityExited: false, newServerIdentityExited: false,
  studioExitCode: null, studioExitSignal: null, allFourPortsReleased: false };

function check(final = false) {
  if (Date.now() >= (final ? cleanupDeadline : mainDeadline)) throw new Error('Native Studio wall-clock budget exhausted.');
  if (!final) cancellation.signal.throwIfAborted();
  if (asynchronousFailure && !final) throw asynchronousFailure;
}

function safeMessage(error) {
  let value = String(error?.message ?? error).slice(0, 4000);
  const expires = Date.now() + 1000;
  for (const secret of [...secretValues].slice(0, 16)) {
    if (Date.now() >= expires) return 'Error message redaction deadline exceeded.';
    value = value.split(secret).join('[redacted]');
  }
  return value.replace(/(?:bearer\s+)[A-Za-z0-9_.-]+/giu, 'Bearer [redacted]')
    .replace(/\b[0-9a-f]{48}\b/giu, '[redacted bridge credential]');
}

async function evidence(name, value, { signal, terminal = false } = {}) {
  // WB-41 uses 46 retained files (including four external acknowledgements,
  // the input and output SQL files). The total remains 48; six slots remain
  // reserved for independent terminal writes. The old lifecycle cap is intact.
  const limit = terminal ? 48 : sqlDialogsRequested ? 42 : 32;
  if (!/^[a-z0-9.-]+$/u.test(name) || counters.filesWritten >= limit || counters.fileWriteAttempts >= limit) throw new Error('Evidence filename/count cap exceeded.');
  counters.fileWriteAttempts += 1;
  const text = encodeNativeEvidence(value, secretValues, { deadline: Math.min(Date.now() + 1000, totalDeadline) });
  await writeFile(path.join(runRoot, name), text, { flag: 'wx', signal: signal ?? (terminal ? undefined : cancellation.signal) });
  counters.filesWritten += 1;
}

function key(identity) { return `${identity.processId}:${identity.creationTimeUtc}`; }
function sameIdentity(a, b) {
  return a && b && a.processId === b.processId && a.parentProcessId === b.parentProcessId
    && a.creationTimeUtc === b.creationTimeUtc && a.commandLine === b.commandLine && a.executablePath === b.executablePath;
}

async function processAction(action, payload, final = false) {
  check(final);
  if (counters.helpers >= (final ? 64 : 40)) throw new Error('PowerShell helper invocation cap exceeded (24 calls reserved for cleanup).');
  counters.helpers += 1;
  const args = ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', helper, '-Action', action];
  const input = JSON.stringify(payload);
  if (Buffer.byteLength(input) >= 524_288) throw new Error('Process payload size cap exceeded.');
  const child = spawn(pwsh, args, { cwd: repository, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const record = { processId: child.pid, parentProcessId: process.pid, startedAtUtc: new Date().toISOString(), command: [pwsh, ...args], action, identity: null, exitCode: null };
  helpers.push(record);
  child.stdin.on('error', () => {});
  child.stdin.end(input);
  let output = '';
  let stderrBytes = 0;
  let exceeded = false;
  let handshakeBuffer = '';
  child.stdout.on('data', (buffer) => {
    if (Buffer.byteLength(output) + buffer.length > 524_288) exceeded = true;
    else output += buffer.toString('utf8');
    if (!record.identity && handshakeBuffer.length < 262_144) {
      handshakeBuffer += buffer.toString('utf8');
      const end = handshakeBuffer.indexOf('\n');
      if (end >= 0) {
        try {
          const message = JSON.parse(handshakeBuffer.slice(0, end));
          if (message.kind === 'helper' && message.identity.processId === child.pid && message.identity.parentProcessId === process.pid && /^7\./u.test(message.version)) {
            record.identity = message.identity;
          }
        } catch { exceeded = true; }
      }
    }
  });
  child.stderr.on('data', (buffer) => { stderrBytes += buffer.length; });
  const helperTimeout = Math.min(25_000, (final ? cleanupDeadline : mainDeadline) - Date.now());
  let helperTimer;
  const code = await new Promise((resolve, reject) => {
    helperTimer = setTimeout(() => {
      // Finally reclaims a timed-out helper through a fresh CIM identity check.
      // If the handshake never arrived, preserve it and report incomplete
      // ownership rather than terminating an unverified PID.
      record.timedOut = true;
      reject(new Error(`Owned PowerShell ${action} helper exceeded its ${helperTimeout}ms deadline.`));
    }, helperTimeout);
    child.once('error', reject);
    child.once('exit', (exitCode) => { record.exitCode = exitCode; record.exitedAtUtc = new Date().toISOString(); });
    child.once('close', (exitCode) => resolve(exitCode));
  }).finally(() => clearTimeout(helperTimer));
  if (exceeded) throw new Error('PowerShell helper output cap exceeded.');
  const lines = output.trim().split(/\r?\n/u);
  if (lines.length > 3) throw new Error('Unexpected PowerShell helper output.');
  const messages = lines.filter(Boolean).map((line) => JSON.parse(line));
  const handshake = messages.find((item) => item.kind === 'helper');
  record.identity = handshake?.identity ?? record.identity;
  record.stderrBytes = stderrBytes;
  if (!record.identity || record.identity.processId !== child.pid || record.identity.parentProcessId !== process.pid || !String(handshake.version).startsWith('7.')) {
    throw new Error('PowerShell 7 helper launch identity could not be proved.');
  }
  const result = messages.find((item) => item.kind === 'result');
  if (code !== 0 || !result) throw new Error(messages.find((item) => item.kind === 'error')?.message ?? `Process helper ${action} failed.`);
  return result.result;
}

async function snapshot(processIds, descendants = false, final = false) {
  const result = await processAction('snapshot', { processIds: [...new Set(processIds)].slice(0, 8), descendants }, final);
  if (!Array.isArray(result.identities) || result.identities.length > 64) throw new Error('Process snapshot count cap exceeded.');
  return result.identities;
}

async function captureOwned(label, final = false) {
  if (!studioIdentity) return [];
  const live = await snapshot([studioIdentity.processId], true, final);
  const root = live.find((item) => item.processId === studioIdentity.processId);
  if (root && !sameIdentity(root, studioIdentity)) throw new Error('The Studio PID was replaced.');
  if (root) {
    for (const identity of live) {
      check(final);
      if (identity.processId !== root.processId) {
        const ancestor = identity.parentChain?.find((item) => sameIdentity(item, studioIdentity));
        if (!ancestor || Date.parse(identity.creationTimeUtc) < Date.parse(root.creationTimeUtc)) throw new Error('Descendant ownership could not be established.');
      }
      if (owned.size >= maxOwnedProcesses && !owned.has(key(identity))) throw new Error('Owned process identity cap exceeded; remaining processes require parent review.');
      owned.set(key(identity), identity);
    }
  }
  records.push({ event: label, atUtc: new Date().toISOString(), identities: live });
  if (records.length > 24) throw new Error('Process event cap exceeded.');
  return live;
}

async function api(method, apiPath, body, auth) {
  check();
  if (++counters.requests > 80) throw new Error('Real API/CDP request cap exceeded.');
  const controller = new AbortController();
  const stop = () => controller.abort();
  cancellation.signal.addEventListener('abort', stop, { once: true });
  const requestTimeout = setTimeout(stop, Math.min(5000, mainDeadline - Date.now()));
  try {
    const response = await fetch(`${origin}${apiPath}`, { method, signal: controller.signal,
      headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), ...(auth ? { Authorization: `Bearer ${auth}` } : {}) },
      body: body ? JSON.stringify(body) : undefined });
    if (!response.ok) throw new Error(`Real API ${method} ${apiPath} returned ${response.status}.`);
    const text = await response.text();
    if (Buffer.byteLength(text) > 524_288) throw new Error('Real API response size cap exceeded.');
    return apiPath === '/healthz' ? { healthyHttpStatus: response.status } : text ? JSON.parse(text) : null;
  } finally {
    clearTimeout(requestTimeout);
    cancellation.signal.removeEventListener('abort', stop);
  }
}

async function boundedPoll(label, test, { attempts = 20, timeoutMs = 20_000, intervalMs = 500, final = false } = {}) {
  const expires = Math.min(Date.now() + timeoutMs, final ? cleanupDeadline : mainDeadline);
  let last;
  for (let attempt = 0; attempt < attempts && Date.now() < expires; attempt += 1) {
    check(final);
    try { const result = await test(); if (result) return result; } catch (error) { last = error; }
    if (attempt % 10 === 0) console.log(`${label}: attempt ${attempt + 1}/${attempts}`);
    await delay(Math.min(intervalMs, Math.max(1, expires - Date.now())), undefined, final ? {} : { signal: cancellation.signal });
  }
  throw new Error(`${label} failed within ${attempts} attempts/${timeoutMs}ms${last ? `: ${safeMessage(last)}` : '.'}`);
}

async function portFree(port, final = false) {
  check(final);
  return new Promise((resolve, reject) => {
    const listener = net.createServer();
    const portTimer = setTimeout(() => { listener.close(); reject(new Error('Loopback bind check timed out.')); }, 1500);
    listener.once('error', (error) => { clearTimeout(portTimer); if (error.code === 'EADDRINUSE') resolve(false); else reject(error); });
    listener.listen({ host: '127.0.0.1', port, exclusive: true }, () => listener.close(() => { clearTimeout(portTimer); resolve(true); }));
  });
}

function status(body) {
  return { isRunning: body.isRunning, startedByStudio: body.startedByStudio, healthy: body.healthy,
    processId: body.processId, url: body.url, dataRoot: body.dataRoot, processOwner: body.processOwner,
    lifecycleState: body.lifecycleState, canStop: body.canStop, hasError: Boolean(body.error) };
}

function assertOwnedRunning(body) {
  if (body.isRunning !== true || body.startedByStudio !== true || body.healthy !== true || body.processOwner !== 'studio'
    || body.lifecycleState !== 'running' || body.canStop !== true || !Number.isInteger(body.processId) || body.processId <= 0
    || body.url !== origin || path.resolve(body.dataRoot).toLowerCase() !== dataRoot.toLowerCase()) {
    throw new Error('Real bridge status did not prove a healthy Studio-owned isolated Server.');
  }
}

function watchPage() {
  page.on('pageerror', () => { counters.pageErrors += 1; });
  page.on('request', () => {
    if (++counters.pageRequests > 240) asynchronousFailure = new Error('Native page request cap exceeded.');
  });
  page.on('response', (response) => {
    const url = new URL(response.url());
    if (url.origin !== bridgeOrigin || !['/studio-bridge/manifest', '/studio-bridge/connections', '/studio-bridge/server/status', '/studio-bridge/server/start', '/studio-bridge/server/stop'].includes(url.pathname)) return;
    if (++counters.bridgeResponses > 64) { asynchronousFailure = new Error('Bridge response cap exceeded.'); return; }
    // Only a public response body whitelist is retained. No request headers,
    // bootstrap event values, Console messages, HAR or raw trace is recorded.
    void response.json().then((body) => {
      let publicBody;
      if (url.pathname.endsWith('/manifest')) publicBody = { mode: body.mode, version: body.version, serverUrl: body.serverUrl,
        managedServerUrl: body.managedServerUrl, dataRoot: body.dataRoot, capabilities: Array.isArray(body.capabilities) ? body.capabilities.slice(0, 32) : [], managedServer: status(body.managedServer ?? {}) };
      else if (url.pathname.endsWith('/connections')) publicBody = { activeProfileId: body.activeProfileId, activeDatabase: body.activeDatabase,
        activeIdentity: body.activeIdentity ? { host: body.activeIdentity.host, profileId: body.activeIdentity.profileId, baseUrl: body.activeIdentity.baseUrl, database: body.activeIdentity.database } : null };
      else publicBody = status(body);
      bridgeEvidence.push({ sequence: bridgeEvidence.length + 1, atUtc: new Date().toISOString(), method: response.request().method(), path: url.pathname, httpStatus: response.status(), body: publicBody });
    }).catch(() => { asynchronousFailure = new Error('A real bridge response could not be decoded.'); });
  });
}

async function latestBridge(apiPath, after = 0) {
  return boundedPoll(`Real bridge ${apiPath}`, async () => bridgeEvidence.slice(after).findLast((item) => item.path === apiPath && item.httpStatus === 200), { attempts: 30, timeoutMs: 15_000, intervalMs: 250 });
}

function watchSqlDialogs() {
  if (!sqlDialogsRequested) return;
  const requests = new WeakMap();
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (url.origin !== bridgeOrigin || !['/studio-bridge/dialogs/open-file', '/studio-bridge/dialogs/save-file'].includes(url.pathname)) return;
    const phase = activeSqlDialog;
    if (!phase || phase.request || phase.expectedPath !== url.pathname || request.method() !== 'POST') {
      asynchronousFailure = new Error('Unexpected or duplicate real SQL dialog bridge request.'); return;
    }
    try {
      const text = request.postData() ?? '';
      if (Buffer.byteLength(text) > 8192) throw new Error('SQL dialog request byte cap exceeded.');
      const body = JSON.parse(text);
      const expectedFilters = phase.kind === 'open'
        ? [{ name: 'SQL files', extensions: ['sql'] }, { name: 'Text files', extensions: ['txt'] }]
        : [{ name: 'SQL files', extensions: ['sql'] }];
      if (body.title !== phase.title || JSON.stringify(body.filters) !== JSON.stringify(expectedFilters)
        || (phase.kind === 'open' && body.maxBytes !== 4 * 1024 * 1024)
        || (phase.kind === 'save' && (body.content !== sqlDialogFixture || body.contentType !== 'application/sql; charset=utf-8'
          || body.suggestedName !== path.basename(sqlDialogPaths.input)))) throw new Error('SQL dialog request disagreed with the existing public contract.');
      // Never retain headers, credentials or arbitrary file content.
      phase.request = { method: request.method(), path: url.pathname, title: body.title, filters: expectedFilters,
        ...(phase.kind === 'open' ? { maxBytes: body.maxBytes }
          : { suggestedName: body.suggestedName, content: sqlDialogFixture, contentType: body.contentType }) };
      requests.set(request, phase);
    } catch (error) { asynchronousFailure = error; }
  });
  page.on('response', (response) => {
    const phase = requests.get(response.request());
    if (!phase) return;
    if (phase.response || ++counters.bridgeResponses > 64) {
      asynchronousFailure = new Error('SQL dialog bridge response count cap exceeded.'); return;
    }
    void response.text().then((text) => {
      if (Buffer.byteLength(text) > 524_288) throw new Error('SQL dialog response byte cap exceeded.');
      const body = JSON.parse(text);
      const expectedName = path.basename(phase.kind === 'open' ? sqlDialogPaths.input : sqlDialogPaths.output);
      phase.response = { httpStatus: response.status(), canceled: body.canceled,
        fileName: body.fileName === expectedName ? expectedName : body.fileName == null ? null : '[unexpected filename omitted]',
        error: body.error == null ? null : '[bridge error text omitted]',
        ...(phase.kind === 'open' ? { content: body.content === sqlDialogFixture ? sqlDialogFixture : body.content == null ? null : '[unexpected content omitted]' } : {}) };
    }).catch((error) => { asynchronousFailure = error; });
  });
}

async function sqlDomState() {
  check();
  // Read ordinary rendered SQL lines/tabs only; no CodeMirror/Vue internals.
  let timer;
  try { return await Promise.race([page.evaluate(() => {
    const tabNodes = [...document.querySelectorAll('.workspace-tabs__scroll [role="tab"]')];
    const editors = [...document.querySelectorAll('.sql-editor .cm-content')];
    const lines = editors.length === 1 ? [...editors[0].querySelectorAll('.cm-line')] : [];
    const successNodes = [...document.querySelectorAll('.n-message--success .n-message__content')];
    if (tabNodes.length > 16 || editors.length !== 1 || lines.length > 64 || successNodes.length > 16) throw new Error('SQL DOM evidence item cap or editor uniqueness failed.');
    const sql = lines.map((line) => line.textContent ?? '').join('\n');
    if (sql.length > 8192) throw new Error('SQL DOM text cap exceeded.');
    return { tabs: tabNodes.map((tab) => ({ label: (tab.querySelector('span')?.textContent ?? '').slice(0, 256), selected: tab.getAttribute('aria-selected') === 'true' })),
      sql, successMessages: successNodes.map((node) => (node.textContent ?? '').slice(0, 512)),
      errorMessageCount: document.querySelectorAll('.n-message--error').length,
      hostIdentity: document.querySelector('[data-testid="studio-host-identity"]')?.textContent ?? null,
      managedState: document.querySelector('[data-testid="studio-managed-state"]')?.textContent ?? null };
  }), new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('SQL DOM evidence read exceeded its 2s deadline.')), Math.min(2000, Math.max(1, mainDeadline - Date.now()))); })]); }
  finally { clearTimeout(timer); }
}

async function observeSqlDialogResult(phase) {
  if (!phase.response) return null;
  check();
  if ((phase.domSampleCount = (phase.domSampleCount ?? 0) + 1) > 140 || Date.now() >= phase.expires) throw new Error('SQL dialog DOM observation count/deadline cap exceeded.');
  const rendered = await sqlDomState();
  if (rendered.errorMessageCount) phase.errorMessageObserved = true;
  if (phase.action === 'cancel' && rendered.successMessages.length) phase.unexpectedSuccessObserved = true;
  const expectedMessage = `已${phase.kind === 'open' ? '打开' : '保存'} ${path.basename(phase.kind === 'open' ? sqlDialogPaths.input : sqlDialogPaths.output)}`;
  const matchingSqlTabs = rendered.sql === sqlDialogFixture && (phase.kind === 'open'
    ? rendered.tabs.length === phase.before.tabs.length + 1 && rendered.tabs.some((tab) => tab.selected && tab.label === path.basename(sqlDialogPaths.input))
    : JSON.stringify(rendered.tabs) === JSON.stringify(phase.before.tabs));
  if (phase.action !== 'cancel' && matchingSqlTabs && rendered.successMessages.includes(expectedMessage)) phase.successObservation ??= rendered;
  return rendered;
}

async function dialogFileBytes(file) {
  check();
  if (![sqlDialogPaths.input, sqlDialogPaths.output].includes(file) || path.dirname(file) !== runRoot
    || (await lstat(file)).isSymbolicLink()) throw new Error('SQL dialog file ownership/path failed.');
  const details = await stat(file);
  if (!details.isFile() || details.size > 8192) throw new Error('SQL dialog evidence file byte/type cap exceeded.');
  const bytes = await readFile(file, { signal: cancellation.signal });
  const hasUtf8Bom = bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf;
  const content = bytes.subarray(hasUtf8Bom ? 3 : 0).toString('utf8');
  if (content !== sqlDialogFixture) throw new Error('The owned SQL file does not contain the exact known fixture.');
  return { path: file, bytes: bytes.length, hasUtf8Bom, sha256: createHash('sha256').update(bytes).digest('hex'), content };
}

async function ownedSqlOutputNames() {
  check();
  const entries = await readdir(runRoot, { withFileTypes: true });
  if (entries.length > 48) throw new Error('Run-root evidence entry cap exceeded.');
  return entries.filter((entry) => /\.(?:sql|txt)$/iu.test(entry.name)).map((entry) => entry.name).sort();
}

async function waitDialogAck(index, phase, expires) {
  const file = path.join(runRoot, `dialog-ack-${index}.json`);
  // Both attempts and wall clock are fixed; each pause is cancellable.
  for (let attempt = 0; attempt < 120 && Date.now() < expires; attempt += 1) {
    check();
    // Native success toasts can normally expire before the parent's screenshot
    // and window audit completes. Observe them after the actual response while
    // waiting, retaining their real DOM state rather than requiring a longer
    // toast lifetime or manufacturing a notification.
    await observeSqlDialogResult(phase);
    try {
      const details = await lstat(file);
      if (!details.isFile() || details.isSymbolicLink() || details.size > 8192) throw new Error('SQL dialog acknowledgement type/size cap exceeded.');
      const ack = JSON.parse(await readFile(file, { encoding: 'utf8', signal: cancellation.signal }));
      if (ack.schema !== 'sonnetdb.wb41.dialog-ack.v1' || ack.runId !== runId || ack.phase !== phase.phase
        || ack.studioIdentityKey !== key(studioIdentity) || ack.action !== phase.action
        || ack.osWindow?.title !== phase.title || !['string', 'number'].includes(typeof ack.osWindow?.id)
        || typeof ack.osWindow?.app !== 'string' || ack.osWindow.app.length > 256
        || typeof ack.observedAfter !== 'string' || ack.observedAfter.length > 512
        || !Number.isFinite(Date.parse(ack.acknowledgedAtUtc))
        || Date.parse(ack.acknowledgedAtUtc) < phase.triggeredAt || Date.parse(ack.acknowledgedAtUtc) > expires) {
        throw new Error('SQL dialog acknowledgement does not match the current native phase/identity.');
      }
      if (++counters.filesWritten > 42) throw new Error('Dialog acknowledgement consumed the terminal file reserve.');
      return { schema: ack.schema, runId: ack.runId, phase: ack.phase, studioIdentityKey: ack.studioIdentityKey,
        osWindow: { id: ack.osWindow.id, app: ack.osWindow.app, title: ack.osWindow.title }, action: ack.action,
        observedAfter: ack.observedAfter, acknowledgedAtUtc: ack.acknowledgedAtUtc };
    } catch (error) { if (error.code !== 'ENOENT') throw error; }
    if (attempt % 20 === 0) console.log(`WB-41 ${phase.phase}: awaiting ordinary OS picker action (${attempt + 1}/120).`);
    await delay(Math.min(500, Math.max(1, expires - Date.now())), undefined, { signal: cancellation.signal });
  }
  throw new Error(`WB-41 ${phase.phase} OS acknowledgement missed its 120 attempts/60s deadline.`);
}

async function runSqlDialogs() {
  check();
  // Select the existing SQL tab using its normal visible DOM control.
  await page.locator('.workspace-tabs__scroll [role="tab"]').first().click({ timeout: 5000 });
  await page.locator('.sql-editor .cm-content').waitFor({ state: 'visible', timeout: 5000 });
  await writeFile(sqlDialogPaths.input, sqlDialogFixture, { flag: 'wx', encoding: 'utf8', signal: cancellation.signal });
  counters.filesWritten += 1;
  const input = await dialogFileBytes(sqlDialogPaths.input);
  if (JSON.stringify(await ownedSqlOutputNames()) !== JSON.stringify([path.basename(sqlDialogPaths.input)])) throw new Error('SQL fixture run root was not initially isolated.');
  const phases = [
    { phase: 'open-success', kind: 'open', action: 'open' },
    { phase: 'open-cancel', kind: 'open', action: 'cancel' },
    { phase: 'save-success', kind: 'save', action: 'save' },
    { phase: 'save-cancel', kind: 'save', action: 'cancel' },
  ];
  const phaseDeadline = Math.min(Date.now() + 290_000, mainDeadline);
  for (let position = 0; position < phases.length && position < 4; position += 1) {
    check();
    if (Date.now() >= phaseDeadline) throw new Error('Four SQL dialog phases exceeded their total wall-clock cap.');
    const phase = { ...phases[position], title: phases[position].kind === 'open' ? '打开 SQL 文件' : '保存 SQL 文件',
      expectedPath: `/studio-bridge/dialogs/${phases[position].kind}-file`, request: null, response: null, passed: false };
    stage = `normal SQL keyboard ${phase.phase}`;
    await boundedPoll('Previous normal success message expiry', async () => (await sqlDomState()).successMessages.length === 0,
      { attempts: 32, timeoutMs: 8000, intervalMs: 250 });
    const before = await sqlDomState();
    const beforeNames = await ownedSqlOutputNames();
    if (position > 0 && before.sql !== sqlDialogFixture) throw new Error('Imported SQL was not retained before the next normal dialog action.');
    phase.before = before;
    phase.input = input;
    phase.beforeOutput = savedSqlBytes ?? null;
    phase.triggeredAt = Date.now();
    const expires = Math.min(phase.triggeredAt + 60_000, mainDeadline, phaseDeadline);
    phase.expires = expires;
    activeSqlDialog = phase;
    sqlDialogPhases.push(phase);
    await page.locator('.sql-editor .cm-content').click({ timeout: 5000 });
    await page.keyboard.press(phase.kind === 'open' ? 'Control+o' : 'Control+s');
    await evidence(`dialog-await-${position + 1}.json`, { schema: 'sonnetdb.wb41.dialog-await.v1', runId, phase: phase.phase,
      studioIdentityKey: key(studioIdentity), studio: { processId: studioIdentity.processId, parentProcessId: studioIdentity.parentProcessId,
        creationTimeUtc: studioIdentity.creationTimeUtc }, title: phase.title, expectedAction: phase.action,
      ...(phase.kind === 'open' ? { inputPath: sqlDialogPaths.input } : { outputPath: sqlDialogPaths.output }),
      before, triggeredAtUtc: new Date(phase.triggeredAt).toISOString(), deadlineUtc: new Date(expires).toISOString(),
      boundary: 'Normal DOM keyboard was issued for the intended OS picker. Actual OS window presence, parent computer-use action and phase acceptance require independent evidence.' });
    phase.ack = await waitDialogAck(position + 1, phase, expires);
    await boundedPoll('Actual SQL dialog bridge response and rendered result', async () => {
      if (!phase.request || !phase.response) return false;
      const rendered = await observeSqlDialogResult(phase);
      if (phase.action === 'cancel') return !phase.unexpectedSuccessObserved && rendered.successMessages.length === 0 && rendered.sql === before.sql
        && JSON.stringify(rendered.tabs) === JSON.stringify(before.tabs);
      if (phase.kind === 'open') return rendered.sql === sqlDialogFixture && rendered.tabs.length === before.tabs.length + 1
        && rendered.tabs.some((tab) => tab.selected && tab.label === path.basename(sqlDialogPaths.input))
        && phase.successObservation?.sql === sqlDialogFixture;
      return rendered.sql === sqlDialogFixture && JSON.stringify(rendered.tabs) === JSON.stringify(before.tabs)
        && phase.successObservation?.sql === sqlDialogFixture;
    }, { attempts: 20, timeoutMs: Math.min(8000, Math.max(1, expires - Date.now())), intervalMs: 250 });
    phase.after = await sqlDomState();
    if (phase.kind === 'save' && phase.action === 'save') {
      savedSqlBytes = await dialogFileBytes(sqlDialogPaths.output);
      if (++counters.filesWritten > 42) throw new Error('Saved SQL consumed the terminal file reserve.');
    }
    phase.afterOutput = savedSqlBytes ? await dialogFileBytes(sqlDialogPaths.output) : null;
    phase.afterInput = await dialogFileBytes(sqlDialogPaths.input);
    phase.outputNames = await ownedSqlOutputNames();
    const expectedNames = [path.basename(sqlDialogPaths.input), ...(savedSqlBytes ? [path.basename(sqlDialogPaths.output)] : [])].sort();
    const expectedCanceled = phase.action === 'cancel';
    const dto = phase.response;
    phase.passed = dto.httpStatus === 200 && dto.canceled === expectedCanceled && dto.error === null
      && (expectedCanceled ? dto.fileName === null && (phase.kind !== 'open' || dto.content === null)
        : dto.fileName === path.basename(phase.kind === 'open' ? sqlDialogPaths.input : sqlDialogPaths.output)
          && (phase.kind !== 'open' || dto.content === sqlDialogFixture))
      && !phase.errorMessageObserved && !phase.unexpectedSuccessObserved && phase.after.errorMessageCount === 0 && phase.afterInput.sha256 === input.sha256
      && JSON.stringify(phase.outputNames) === JSON.stringify(expectedNames)
      && (!expectedCanceled || JSON.stringify(beforeNames) === JSON.stringify(phase.outputNames))
      && (phase.phase !== 'save-cancel' || phase.afterOutput.sha256 === phase.beforeOutput.sha256);
    // DTO, ordinary DOM and exact disk bytes/BOM evidence remain separate from
    // the six lifecycle geometry observations; one JSON and PNG per phase.
    await evidence(`dialog-phase-${position + 1}.json`, { schema: 'sonnetdb.wb41.dialog-phase.v1', runId, ...phase,
      boundary: 'Known SQL fixture only; no SQL execution, arbitrary user file, credentials, private state or synthetic event.' });
    if (++counters.filesWritten > 42) throw new Error('SQL DOM screenshot consumed the terminal file reserve.');
    await page.screenshot({ path: path.join(runRoot, `dialog-phase-${position + 1}.png`), fullPage: false, timeout: 5000 });
    activeSqlDialog = undefined;
    if (!phase.passed) throw new Error(`WB-41 ${phase.phase} did not prove the real DTO/DOM/disk boundary.`);
  }
}

async function observeNativeWindow(label) {
  check();
  if (nativeWindowObservations.length >= 6) throw new Error('Native window observation cap exceeded.');
  let geometryTimer;
  const geometry = await Promise.race([
    // Read ordinary DOM geometry only; this never changes the native viewport,
    // CSS, bootstrap state or host contract.
    page.evaluate(() => {
      const viewport = { innerWidth: window.innerWidth, innerHeight: window.innerHeight, devicePixelRatio: window.devicePixelRatio,
        scrollX: window.scrollX, scrollY: window.scrollY,
        visualViewport: window.visualViewport ? { width: window.visualViewport.width, height: window.visualViewport.height,
          scale: window.visualViewport.scale, offsetLeft: window.visualViewport.offsetLeft, offsetTop: window.visualViewport.offsetTop } : null };
      const extent = (element) => element ? { clientWidth: element.clientWidth, scrollWidth: element.scrollWidth,
        horizontalOverflow: element.scrollWidth > element.clientWidth, overflowX: getComputedStyle(element).overflowX } : null;
      const elementGeometry = (element) => {
        if (!element) return { present: false, boundingBox: null, inViewport: false, ancestors: [] };
        const rect = element.getBoundingClientRect();
        const centerHit = document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2);
        return { present: true, boundingBox: { x: rect.x, y: rect.y, width: rect.width, height: rect.height },
          inViewport: rect.width > 0 && rect.height > 0 && rect.left >= 0 && rect.top >= 0
            && rect.right <= window.innerWidth && rect.bottom <= window.innerHeight,
          centerHitMatches: Boolean(centerHit && (centerHit === element || element.contains(centerHit))),
          ancestors: [extent(element.parentElement), extent(element.parentElement?.parentElement)] };
      };
      const control = (testId) => ({ testId, ...elementGeometry(document.querySelector(`[data-testid="${testId}"]`)) });
      return { viewport, document: extent(document.documentElement), body: extent(document.body),
        identity: document.querySelector('[data-testid="studio-host-identity"]')?.innerText ?? null,
        state: document.querySelector('[data-testid="studio-managed-state"]')?.innerText ?? null,
        identityGeometry: control('studio-host-identity'), stateGeometry: control('studio-managed-state'),
        contractWarning: Boolean(document.querySelector('[data-testid="studio-managed-contract-warning"]')),
        explorer: { collapseToggleCount: document.querySelectorAll('[title="收起资源浏览器"]').length,
          expandToggleCount: document.querySelectorAll('[title="展开资源浏览器"]').length,
          collapseToggle: elementGeometry(document.querySelector('[title="收起资源浏览器"]')) },
        controls: { health: control('studio-managed-health'), start: control('studio-managed-start'), stop: control('studio-managed-stop') } };
    }),
    new Promise((_, reject) => { geometryTimer = setTimeout(() => reject(new Error('Read-only native DOM geometry exceeded 3000ms.')), 3000); }),
  ]).finally(() => clearTimeout(geometryTimer));
  const controlNames = ['health', 'start', 'stop'];
  const expires = Math.min(Date.now() + 6000, mainDeadline);
  for (let index = 0; index < controlNames.length && index < 3; index += 1) {
    check();
    if (Date.now() >= expires) throw new Error('Native control observation deadline exceeded.');
    const control = geometry.controls[controlNames[index]];
    const locator = page.getByTestId(control.testId).first();
    control.isVisible = control.present && await locator.isVisible();
    control.isEnabled = control.present && await locator.isEnabled({ timeout: Math.max(1, Math.min(1000, expires - Date.now())) });
  }
  const collapseToggle = page.getByTitle('收起资源浏览器', { exact: true });
  geometry.explorer.collapseToggle.isVisible = geometry.explorer.collapseToggleCount === 1 && await collapseToggle.isVisible();
  geometry.explorer.collapseToggle.isEnabled = geometry.explorer.collapseToggleCount === 1 && await collapseToggle.isEnabled({ timeout: 1000 });
  geometry.identityGeometry.isVisible = geometry.identityGeometry.present && await page.getByTestId('studio-host-identity').first().isVisible();
  geometry.stateGeometry.isVisible = geometry.stateGeometry.present && await page.getByTestId('studio-managed-state').first().isVisible();
  const observation = { label, atUtc: new Date().toISOString(), requestedNativeWindow: windowConfiguration,
    viewportInjected: false, ...geometry };
  nativeWindowObservations.push(observation);
  return observation;
}

function assertNativeWindow(observation) {
  const viewport = observation.viewport;
  if (!Number.isFinite(viewport.innerWidth) || viewport.innerWidth <= 0 || !Number.isFinite(viewport.innerHeight) || viewport.innerHeight <= 0
    || !Number.isFinite(viewport.devicePixelRatio) || viewport.devicePixelRatio <= 0) throw new Error('Actual native CSS viewport dimensions/DPR are invalid.');
  if (windowMode === 'narrow' && viewport.innerWidth > 1100) throw new Error('The requested narrow native window did not produce an actual CSS viewport at or below 1100px.');
}

function assertNativeControl(observation, name) {
  const control = observation.controls[name];
  if (!control.present || !control.isVisible || !control.inViewport || !control.isEnabled) {
    throw new Error(`Normal native DOM ${name} control is absent, clipped, hidden or disabled.`);
  }
}

async function prepareNativeExplorer(observation) {
  check();
  const explorer = observation.explorer;
  nativeUiPreparation.beforeObservation = observation.label;
  nativeUiPreparation.beforeCssWidth = observation.viewport.innerWidth;
  nativeUiPreparation.collapseToggle = explorer.collapseToggle;
  nativeUiPreparation.collapseToggleCount = explorer.collapseToggleCount;
  nativeUiPreparation.needed = observation.viewport.innerWidth <= 1099
    && ((!observation.identityGeometry.isVisible || !observation.identityGeometry.inViewport || !observation.identityGeometry.centerHitMatches)
      || (!observation.stateGeometry.isVisible || !observation.stateGeometry.inViewport || !observation.stateGeometry.centerHitMatches));
  // At most one ordinary, visible control click prepares the existing narrow
  // Explorer layout. The pre-collapse observation remains separate evidence.
  if (nativeUiPreparation.needed) {
    if (explorer.collapseToggleCount !== 1 || !explorer.collapseToggle.isVisible || !explorer.collapseToggle.isEnabled
      || !explorer.collapseToggle.inViewport) {
      await evidence('native-ui-preparation.json', nativeUiPreparation);
      throw new Error('The existing Explorer overlay needs collapse, but its ordinary visible toggle is unavailable.');
    }
    try {
      await page.getByTitle('收起资源浏览器', { exact: true }).click({ timeout: 5000 });
      nativeUiPreparation.clicked = true;
      await page.getByTitle('展开资源浏览器', { exact: true }).waitFor({ state: 'visible', timeout: 5000 });
      nativeUiPreparation.collapsedObserved = true;
    } catch (error) { nativeUiPreparation.failure = safeMessage(error); }
  }
  nativeUiPreparation.afterCollapseToggleCount = await page.getByTitle('收起资源浏览器', { exact: true }).count();
  nativeUiPreparation.afterExpandToggleCount = await page.getByTitle('展开资源浏览器', { exact: true }).count();
  nativeUiPreparation.boundary = nativeUiPreparation.clicked ? 'Subsequent fit acceptance covers the normally collapsed Explorer state; uncollapsed reachability is only recorded.'
    : 'No Explorer preparation click was required by the measured native window.';
  await evidence('native-ui-preparation.json', nativeUiPreparation);
  if (nativeUiPreparation.failure) throw new Error(nativeUiPreparation.failure);
}

async function dom(label, expectedState) {
  check();
  let pollFailure;
  try {
    await boundedPoll(`DOM ${label}`, async () => {
      const identity = await page.getByTestId('studio-host-identity').first().textContent({ timeout: 2000 });
      const stateText = await page.getByTestId('studio-managed-state').first().textContent({ timeout: 2000 });
      const action = page.getByTestId(expectedState === 'Studio 运行中' ? 'studio-managed-stop' : 'studio-managed-start').first();
      return identity?.includes('studio-desktop') && identity.includes('managed-local') && identity.includes(origin) && stateText === expectedState
        && await action.isEnabled({ timeout: 1000 }) && await page.getByTestId('studio-managed-health').first().isEnabled({ timeout: 1000 });
    }, { attempts: 20, timeoutMs: 15_000, intervalMs: 250 });
  } catch (error) { pollFailure = safeMessage(error); }
  const observation = await observeNativeWindow(label);
  const body = { label, url: page.url(), identity: observation.identity, state: observation.state,
    canStart: observation.controls.start.present, canStop: observation.controls.stop.present,
    contractWarning: observation.contractWarning, pollFailure: pollFailure ?? null, nativeWindow: observation };
  // Preserve actual clipping/overflow evidence before enforcing acceptance.
  await evidence(`dom-${label}.json`, body);
  check();
  if (++counters.filesWritten > 48) throw new Error('Evidence file cap exceeded.');
  await page.screenshot({ path: path.join(runRoot, `${label}.png`), fullPage: false, timeout: 5000 });
  if (pollFailure) throw new Error(pollFailure);
  if (body.contractWarning || body.canStop !== (expectedState === 'Studio 运行中') || body.canStart !== (expectedState === 'Studio 已停止')) throw new Error('Normal DOM controls disagreed with the real bridge lifecycle contract.');
  assertNativeWindow(observation);
  if (!observation.identityGeometry.isVisible || !observation.identityGeometry.inViewport || !observation.identityGeometry.centerHitMatches
    || !observation.stateGeometry.isVisible || !observation.stateGeometry.inViewport || !observation.stateGeometry.centerHitMatches) {
    throw new Error('Normal native DOM identity/state is hidden, clipped or covered.');
  }
  assertNativeControl(observation, 'health');
  assertNativeControl(observation, expectedState === 'Studio 运行中' ? 'stop' : 'start');
  return body;
}

async function identifyServer(body, label) {
  assertOwnedRunning(body);
  const live = await captureOwned(label);
  const identity = live.find((item) => item.processId === body.processId);
  if (!identity || identity.parentProcessId !== studioIdentity.processId || !identity.commandLine.toLowerCase().includes(serverDll.toLowerCase())
    || !identity.parentChain.some((item) => sameIdentity(item, studioIdentity))) throw new Error('Bridge Server PID did not match an actual Studio-owned Server DLL process.');
  await evidence(`${label}-identity.json`, identity);
  return identity;
}

async function gone(identity, final = false) {
  const live = await snapshot([identity.processId], false, final);
  return !live.some((item) => sameIdentity(item, identity));
}

async function sourceHash(file) {
  const details = await stat(file);
  if (!details.isFile() || details.size > 134_217_728) throw new Error('Named prerequisite file size/type cap exceeded.');
  return { path: file, bytes: details.size, sha256: createHash('sha256').update(await readFile(file)).digest('hex') };
}

function isolatedEnvironment() {
  const environment = { ...process.env };
  const names = Object.keys(environment);
  if (names.length > 2048) throw new Error('Environment count cap exceeded.');
  const expires = Date.now() + 1000;
  for (let index = 0; index < names.length && index < 2048; index += 1) {
    if (Date.now() >= expires) throw new Error('Environment filtering deadline exceeded.');
    if (/^(SONNETDB_|ASPNETCORE_|DOTNET_ENVIRONMENT$|WEBVIEW2_|SonnetDBServer(?:__|:)|Kestrel(?:__|:)|ConnectionStrings(?:__|:)|URLS$)/iu.test(names[index])) delete environment[names[index]];
  }
  return { ...environment, DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production',
    ASPNETCORE_CONTENTROOT: contentRoot, ASPNETCORE_WEBROOT: serverWebRoot,
    SONNETDB_Kestrel__Endpoints__Http__Protocols: 'Http1',
    SONNETDB_Kestrel__Endpoints__FrameH2__Protocols: 'Http2',
    WEBVIEW2_USER_DATA_FOLDER: profileRoot,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-address=127.0.0.1 --remote-debugging-port=${ports.cdp}` };
}

async function removeOwnedDirectory(directory) {
  check(true);
  const expected = path.resolve(directory);
  const resolved = await realpath(directory);
  if (![dataRoot, profileRoot, contentRoot].includes(expected) || path.dirname(expected) !== runRoot || resolved.toLowerCase() !== expected.toLowerCase() || (await lstat(directory)).isSymbolicLink()) throw new Error('Owned directory path verification failed.');
  const markerFile = path.join(directory, markerName);
  if ((await stat(markerFile)).size > 4096) throw new Error('Owned marker size cap exceeded.');
  const marker = JSON.parse(await readFile(markerFile, 'utf8'));
  if (marker.runId !== runId || marker.runnerPid !== process.pid || marker.absolutePath !== expected) throw new Error('Owned directory marker verification failed.');
  const pending = [{ directory, depth: 0 }];
  const files = [];
  const directories = [];
  const scanDeadline = Math.min(Date.now() + 8000, cleanupDeadline);
  let entries = 0;
  for (let index = 0; index < pending.length && index < 512; index += 1) {
    if (Date.now() >= scanDeadline) throw new Error('Owned directory scan deadline exceeded.');
    const current = pending[index];
    const children = await readdir(current.directory, { withFileTypes: true });
    if (children.length > 4096 || current.depth > 16) throw new Error('Owned directory entry/depth cap exceeded.');
    for (const child of children) {
      if (++entries > 4096 || Date.now() >= scanDeadline) throw new Error('Owned directory scan count/deadline exceeded.');
      const childPath = path.join(current.directory, child.name);
      if ((await lstat(childPath)).isSymbolicLink()) throw new Error('Owned directory contains a link; removal refused.');
      if (child.isDirectory()) pending.push({ directory: childPath, depth: current.depth + 1 });
      else if (child.isFile()) files.push(childPath);
      else throw new Error('Owned directory contains an unsupported entry.');
    }
    directories.push(current.directory);
  }
  if (pending.length > 512) throw new Error('Owned directory count cap exceeded.');
  // Scan completes before any deletion. Only these individually checked paths
  // are deleted; there is no broad recursive Remove-Item/rm operation.
  for (const file of files) { check(true); await unlink(file); }
  for (const item of directories.reverse()) { check(true); await rmdir(item); }
  return { path: expected, entriesRemoved: entries + 1, removed: true };
}

try {
  if (process.platform !== 'win32' || !selectedEvidence || !['default', 'narrow'].includes(windowMode)
    || !['lifecycle', 'sql-dialogs'].includes(scenario)
    || (sqlDialogsRequested ? selectedEvidence[0] !== 'wb41' : selectedEvidence[0] === 'wb41')
    || repository.toLowerCase() !== 'd:\\source\\sonnetdb') throw new Error('Run on Windows from D:\\source\\SonnetDB with an explicit WB-39/WB-40 named evidence parent and default|narrow window mode.');
  const prerequisiteFiles = [pwsh, helper, studioExe, studioDll, serverDll, path.join(serverWebRoot, 'index.html'), fileURLToPath(import.meta.url),
    path.join(repository, 'web', 'e2e', 'studio-native-evidence.mjs'), path.join(repository, 'web', 'e2e', 'studio-native-evidence.test.mjs')];
  if (prerequisiteFiles.length > 10) throw new Error('Prerequisite file cap exceeded.');
  for (const file of prerequisiteFiles) { check(); await access(file); }
  for (const port of Object.values(ports)) if (!await portFree(port)) throw new Error(`Required loopback port ${port} is already occupied; no host was started.`);
  await mkdir(evidenceParent, { recursive: true });
  if ((await realpath(evidenceParent)).toLowerCase() !== evidenceParent.toLowerCase() || (await lstat(evidenceParent)).isSymbolicLink()) throw new Error('Evidence parent resolves outside the fixed path.');
  await mkdir(runRoot);
  const hashes = [];
  for (const file of prerequisiteFiles) { check(); hashes.push(await sourceHash(file)); }
  await evidence('run.json', { runId, validationSlice, scenario, evidenceParentSelection: selectedEvidence[0], requestedNativeWindow: windowConfiguration,
    runnerPid: process.pid, startedAtUtc: new Date(startedAt).toISOString(), budgetSeconds: 600, ports, origin, bridgeOrigin, cdpOrigin,
    studioExe, serverDll, contentRoot, dataRoot, profileRoot, serverWebRoot, libraryPath, hashes,
    runtimePrerequisite: 'WebView2 154.0.4258.53 checked by the parent; actual attachment remains required.',
    boundary: sqlDialogsRequested ? 'Validation scope: actual Studio/WebView2 bootstrap, SQL OS pickers, and Managed Local lifecycle. OS picker presence/actions require parent evidence and phase acceptance. API setup/login auth storage only; no login UI, installation, NativeAOT, permission matrix or full three-host parity claim.'
      : 'Actual Studio/WebView2 native bootstrap and Managed Local lifecycle. API setup/login auth storage only; no login UI, OS dialog, installation, NativeAOT, permission matrix or full three-host parity claim.' });
  for (const directory of [profileRoot, dataRoot, contentRoot]) {
    check();
    await mkdir(directory);
    await writeFile(path.join(directory, markerName), JSON.stringify({ runId, runnerPid: process.pid, absolutePath: directory }), { flag: 'wx' });
  }
  await writeFile(path.join(contentRoot, 'appsettings.json'), JSON.stringify({ Logging: { LogLevel: { Default: 'Warning' } }, AllowedHosts: '127.0.0.1',
    SonnetDBServer: { DataRoot: dataRoot, AutoLoadExistingDatabases: true, AllowAnonymousProbes: true, Tokens: {},
      Mqtt: { Enabled: false, Sparkplug: { Enabled: false }, ExternalClient: { Enabled: false } }, Coap: { Enabled: false, Dtls: { Enabled: false } },
      LineProtocolUdp: { Enabled: false }, Modbus: { Enabled: false }, SemanticSearch: { Enabled: false } } }, null, 2), { flag: 'wx' });
  runnerIdentity = (await snapshot([process.pid]))[0];
  if (!runnerIdentity || runnerIdentity.processId !== process.pid) throw new Error('Runner process identity was not inspectable.');
  stage = 'actual Studio launch';
  const args = ['--server-url', origin, '--managed-server-url', origin, '--bridge-port', String(ports.bridge), '--data-root', dataRoot,
    '--connection-library', libraryPath, '--server-exe', serverDll, '--auto-start-server', ...windowConfiguration.dimensionArguments,
    '--route', '/admin/app/sql?tool=table'];
  const environment = isolatedEnvironment();
  await evidence('launch.json', { executable: studioExe, args, requestedNativeWindow: windowConfiguration, parentIdentity: runnerIdentity, windowsHideRequested: false,
    nativeWindowBoundary: 'Ordinary visible launch requested; prior MainWindowHandle=0 did not prove a windowsHide or product defect.',
    childEnvironment: { DOTNET_ENVIRONMENT: environment.DOTNET_ENVIRONMENT, ASPNETCORE_ENVIRONMENT: environment.ASPNETCORE_ENVIRONMENT,
      ASPNETCORE_CONTENTROOT: contentRoot, ASPNETCORE_WEBROOT: serverWebRoot, WEBVIEW2_USER_DATA_FOLDER: profileRoot,
      SONNETDB_Kestrel__Endpoints__Http__Protocols: 'Http1', SONNETDB_Kestrel__Endpoints__FrameH2__Protocols: 'Http2',
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: environment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS } });
  studio = spawn(studioExe, args, { cwd: contentRoot, env: environment, windowsHide: false, stdio: ['ignore', 'pipe', 'pipe'] });
  studio.stdout.on('data', (buffer) => { streamCounts.stdoutBytes += buffer.length; if (streamCounts.stdoutBytes > 4_194_304) asynchronousFailure = new Error('Studio stdout byte cap exceeded.'); });
  studio.stderr.on('data', (buffer) => { streamCounts.stderrBytes += buffer.length; if (streamCounts.stderrBytes > 4_194_304) asynchronousFailure = new Error('Studio stderr byte cap exceeded.'); });
  studio.on('error', () => { asynchronousFailure = new Error('Actual Studio executable could not be started.'); });
  studio.on('exit', (code, signal) => records.push({ event: 'studio-exit', atUtc: new Date().toISOString(), processId: studio.pid, code, signal }));
  studioIdentity = (await snapshot([studio.pid]))[0];
  if (!studioIdentity || studioIdentity.parentProcessId !== process.pid || studioIdentity.executablePath.toLowerCase() !== studioExe.toLowerCase()
    || !studioIdentity.commandLine.includes(dataRoot) || !studioIdentity.parentChain.some((item) => sameIdentity(item, runnerIdentity))) throw new Error('Actual Studio launch ownership could not be proved.');
  owned.set(key(studioIdentity), studioIdentity);
  await evidence('studio-identity.json', studioIdentity);
  await boundedPoll('Managed Server health', async () => { const result = await api('GET', '/healthz'); return result || true; }, { attempts: 30, timeoutMs: 60_000, intervalMs: 1000 });
  stage = 'real setup and API login';
  const setup = await api('GET', '/v1/setup/status');
  if (setup.needsSetup !== true || !setup.suggestedServerId) throw new Error('Managed Local was not a fresh isolated Server.');
  const password = `WB40_${randomBytes(18).toString('hex')}!`;
  const bearerToken = `wb40_${randomBytes(24).toString('hex')}`;
  secretValues.add(password); secretValues.add(bearerToken);
  const administrator = await api('POST', '/v1/setup/initialize', { serverId: setup.suggestedServerId, organization: 'WB40 isolated native desktop evidence', username: 'wb40_admin', password, bearerToken });
  if (administrator.token) secretValues.add(administrator.token);
  if (administrator.tokenId) secretValues.add(administrator.tokenId);
  const identity = await api('POST', '/v1/auth/login', { username: 'wb40_admin', password });
  if (identity.token) secretValues.add(identity.token);
  if (identity.tokenId) secretValues.add(identity.tokenId);
  if (identity.username !== 'wb40_admin' || identity.isSuperuser !== true || !identity.token || !identity.tokenId) throw new Error('Real API login did not return a valid Studio session.');
  await evidence('authentication-boundary.json', { setupWasRequired: true, initializationSucceeded: true, apiLoginSucceeded: true, username: identity.username, isSuperuser: identity.isSuperuser, tokenStoredVia: 'sndb.auth localStorage only', loginUiVerified: false });
  stage = 'actual WebView2 loopback CDP';
  const cdpVersion = await boundedPoll('WebView2 CDP environment passthrough', async () => {
    check();
    if (++counters.requests > 80) throw new Error('API/CDP request cap exceeded.');
    const response = await fetch(`${cdpOrigin}/json/version`, { signal: AbortSignal.timeout(1500) });
    const value = await response.json();
    return response.ok && typeof value.webSocketDebuggerUrl === 'string' && value.webSocketDebuggerUrl.startsWith(`ws://127.0.0.1:${ports.cdp}/`) ? { Browser: value.Browser, 'Protocol-Version': value['Protocol-Version'] } : false;
  }, { attempts: 30, timeoutMs: 45_000, intervalMs: 500 });
  await evidence('actual-cdp-version.json', cdpVersion);
  browser = await chromium.connectOverCDP(cdpOrigin, { timeout: 15_000 });
  if (browser.contexts().length !== 1) throw new Error('Expected one real Studio WebView2 browser context.');
  const context = browser.contexts()[0];
  if (context.pages().length > 4) throw new Error('WebView2 page count cap exceeded.');
  const matching = context.pages().filter((candidate) => {
    try { const url = new URL(candidate.url()); return url.origin === origin && url.pathname.startsWith('/admin/'); } catch { return false; }
  });
  if (matching.length !== 1) throw new Error('A unique actual Studio managed-Server page was not found; no substitute page is created.');
  page = matching[0];
  const initialNativeUrl = page.url();
  page.setDefaultTimeout(8000);
  page.setDefaultNavigationTimeout(15_000);
  await captureOwned('cdp-attached');
  if (![...owned.values()].some((item) => item.commandLine.toLowerCase().includes(profileRoot.toLowerCase()) && item.commandLine.includes(`--remote-debugging-port=${ports.cdp}`))) throw new Error('An actual Studio descendant did not prove the private WebView2 profile/CDP launch.');
  watchPage();
  watchSqlDialogs();
  // The only browser script mutation is a valid identity returned by the real
  // auth API. Navigation in this same native page runs the ordinary routed app
  // and native bootstrap; an initial setup/login redirect is not assumed away.
  await page.evaluate((auth) => localStorage.setItem('sndb.auth', JSON.stringify(auth)), { username: identity.username, token: identity.token, tokenId: identity.tokenId, isSuperuser: identity.isSuperuser });
  await page.goto(`${origin}/admin/app/sql?tool=table`, { waitUntil: 'domcontentloaded' });
  if (new URL(page.url()).origin !== origin || new URL(page.url()).pathname !== '/admin/app/sql') throw new Error('Native main-window URL is outside the normal Workbench route.');
  await evidence('native-window-route.json', { initialNativeUrl, finalNativeUrl: page.url(), sameExistingNativePage: true, substitutePageCreated: false });
  stage = 'normal native window CSS viewport';
  let readinessFailure;
  try {
    await boundedPoll('Public native DOM ready for geometry', async () => await page.getByTestId('studio-host-identity').count() === 1
      && await page.getByTestId('studio-managed-state').count() === 1, { attempts: 20, timeoutMs: 15_000, intervalMs: 250 });
  } catch (error) { readinessFailure = safeMessage(error); }
  const nativeWindow = await observeNativeWindow('initial-native-window');
  await evidence('native-window-viewport.json', { ...nativeWindow, readinessFailure: readinessFailure ?? null,
    boundary: 'Actual native-window dimensions and read-only DOM geometry. Default omits size overrides; only narrow requires measured CSS width at or below 1100px. Document overflow is recorded without extending this lifecycle acceptance.' });
  check();
  if (++counters.filesWritten > 48) throw new Error('Evidence file cap exceeded.');
  await page.screenshot({ path: path.join(runRoot, 'initial-native-window.png'), fullPage: false, timeout: 5000 });
  if (readinessFailure) throw new Error(readinessFailure);
  assertNativeWindow(nativeWindow);
  await prepareNativeExplorer(nativeWindow);
  stage = 'normal DOM native identity and manifest';
  await dom('initial-running', 'Studio 运行中');
  const manifest = await latestBridge('/studio-bridge/manifest');
  if (manifest.body.mode !== 'studio-desktop' || manifest.body.serverUrl !== origin || manifest.body.managedServerUrl !== origin
    || !manifest.body.capabilities.includes('server.managedLocal') || !manifest.body.capabilities.includes('menu.native')) throw new Error('Actual native bootstrap did not produce the Studio desktop manifest.');
  const connections = await latestBridge('/studio-bridge/connections');
  if (connections.body.activeProfileId !== 'managed-local' || connections.body.activeIdentity?.host !== 'studio-desktop'
    || connections.body.activeIdentity?.baseUrl !== origin) throw new Error('Real native connection library identity did not match the DOM.');
  const oldServer = await identifyServer(manifest.body.managedServer, 'initial-server');
  if (sqlDialogsRequested) await runSqlDialogs();
  stage = 'normal DOM Health';
  let after = bridgeEvidence.length;
  await page.getByTestId('studio-managed-health').first().click();
  assertOwnedRunning((await latestBridge('/studio-bridge/server/status', after)).body);
  await dom('health-running', 'Studio 运行中');
  stage = 'normal DOM Stop';
  after = bridgeEvidence.length;
  await captureOwned('before-stop');
  await page.getByTestId('studio-managed-stop').first().click();
  const stopped = (await latestBridge('/studio-bridge/server/stop', after)).body;
  if (stopped.isRunning !== false || stopped.processOwner !== 'none' || stopped.lifecycleState !== 'stopped' || stopped.canStop !== false || stopped.processId != null) throw new Error('Real bridge Stop did not report an unowned stopped state.');
  await dom('stopped', 'Studio 已停止');
  await boundedPoll('Old managed Server PID exit', () => gone(oldServer), { attempts: 6, timeoutMs: 20_000, intervalMs: 500 });
  nativeClose.oldServerIdentityExited = true;
  for (const port of [ports.http, ports.frame]) if (!await portFree(port)) throw new Error('Managed Server Stop did not release its loopback ports.');
  await evidence('stop-result.json', { oldIdentity: oldServer, oldIdentityExited: true, httpAndFramePortsReleased: true, status: stopped });
  stage = 'normal DOM Start';
  after = bridgeEvidence.length;
  await page.getByTestId('studio-managed-start').first().click();
  const restarted = (await latestBridge('/studio-bridge/server/start', after)).body;
  const newServer = await identifyServer(restarted, 'restarted-server');
  if (sameIdentity(newServer, oldServer)) throw new Error('Start did not produce a new actual managed Server identity.');
  await dom('restarted-running', 'Studio 运行中');
  after = bridgeEvidence.length;
  await page.getByTestId('studio-managed-health').first().click();
  const healthy = (await latestBridge('/studio-bridge/server/status', after)).body;
  assertOwnedRunning(healthy);
  if (healthy.processId !== newServer.processId) throw new Error('Health did not retain the restarted actual Server PID.');
  await dom('restarted-health-running', 'Studio 运行中');
  stage = 'normal native main-window exit';
  await captureOwned('before-native-close');
  nativeClose.attempted = true;
  const closed = await processAction('close', { identity: studioIdentity });
  nativeClose.accepted = closed.acted === true;
  nativeClose.discovery = closed.discovery ?? null;
  await evidence('native-close-discovery.json', { studioIdentityKey: key(studioIdentity), ...closed });
  if (closed.acted !== true || closed.method !== 'CloseMainWindow') throw new Error('Normal native main-window close was not accepted.');
  await boundedPoll('Studio normal desktop exit', () => gone(studioIdentity), { attempts: 6, timeoutMs: 25_000, intervalMs: 500 });
  nativeClose.studioIdentityExited = true;
  await boundedPoll('Studio exit event', async () => studio.exitCode !== null || studio.signalCode !== null, { attempts: 10, timeoutMs: 2000, intervalMs: 100 });
  nativeClose.studioExitCode = studio.exitCode;
  nativeClose.studioExitSignal = studio.signalCode;
  if (studio.exitCode !== 0 || studio.signalCode !== null) throw new Error('Native Studio exited abnormally after CloseMainWindow.');
  await boundedPoll('Restarted managed Server exit', () => gone(newServer), { attempts: 6, timeoutMs: 20_000, intervalMs: 500 });
  nativeClose.newServerIdentityExited = true;
  for (const port of Object.values(ports)) if (!await portFree(port)) throw new Error('Normal Studio exit did not release all four loopback ports.');
  normalExit = true;
  nativeClose.allFourPortsReleased = true;
} catch (error) {
  fatal = { stage, message: safeMessage(error) };
  console.error(`${validationSlice} native validation failed at ${stage}: ${fatal.message}`);
} finally {
  clearTimeout(timeout);
  stage = 'owned process cleanup';
  const cleanup = { normalExit, fallbackActions: [], helperReclaims: [], identityChecks: [], directories: [], errors: [], allFourPortsReleased: false };
  const pendingHelpers = helpers.filter((item) => !item.exitedAtUtc).slice(0, 64);
  const helperReclaimDeadline = Math.min(Date.now() + 20_000, cleanupDeadline);
  for (const pending of pendingHelpers) {
    if (Date.now() >= helperReclaimDeadline) { cleanup.errors.push('Helper reclamation deadline exceeded.'); break; }
    try {
      if (!pending.identity) throw new Error(`Helper PID ${pending.processId} has no complete launch identity; preserved for parent review.`);
      cleanup.helperReclaims.push({ identity: pending.identity, result: await processAction('kill', { identity: pending.identity }, true) });
    } catch (error) { cleanup.errors.push(safeMessage(error)); }
  }
  try {
    // CDP is only an attachment. Closing the browser through CDP would change
    // the native lifecycle being measured, so Browser.close() is never called.
    if (studioIdentity && !normalExit) await captureOwned('cleanup-discovery', true);
    const targets = [...owned.values()].sort((a, b) => (b.parentChain?.length ?? 0) - (a.parentChain?.length ?? 0));
    if (targets.length > maxOwnedProcesses) throw new Error('Cleanup target cap exceeded.');
    const snapshotIds = targets.map((item) => item.processId);
    const live = [];
    for (let index = 0; index < snapshotIds.length && index < maxOwnedProcesses; index += 8) live.push(...await snapshot(snapshotIds.slice(index, index + 8), false, true));
    for (const target of targets) {
      check(true);
      const current = live.find((item) => sameIdentity(item, target));
      cleanup.identityChecks.push({ identity: target, exitedBeforeFallback: !current });
      if (current) {
        const result = await processAction('kill', { identity: target }, true);
        cleanup.fallbackActions.push({ identity: target, result });
      }
    }
    const remaining = [];
    for (let index = 0; index < snapshotIds.length && index < maxOwnedProcesses; index += 8) remaining.push(...await snapshot(snapshotIds.slice(index, index + 8), false, true));
    if (targets.some((target) => remaining.some((item) => sameIdentity(item, target)))) throw new Error('A recorded owned process remains alive after cleanup.');
    for (const port of Object.values(ports)) if (!await portFree(port, true)) throw new Error('A required loopback port remains occupied after cleanup.');
    cleanup.allFourPortsReleased = true;
    if (studio && !studioIdentity && studio.exitCode === null) throw new Error('Studio ownership was not established; process and directories are preserved for manual review.');
    if (helpers.some((item) => !item.exitedAtUtc)) throw new Error('An owned helper has not proved exit; parent process review is required.');
    cleanupProven = cleanup.errors.length === 0;
    for (const directory of [profileRoot, dataRoot, contentRoot]) {
      try { await access(directory); cleanup.directories.push(await removeOwnedDirectory(directory)); }
      catch (error) { if (error.code !== 'ENOENT') cleanup.errors.push(safeMessage(error)); }
    }
  } catch (error) { cleanup.errors.push(safeMessage(error)); }
  cleanupProven = cleanupProven && cleanup.errors.length === 0;
  if (normalExit && cleanup.fallbackActions.length) fatal ??= { stage: 'normal exit child-process audit', message: 'A Studio-owned descendant survived normal desktop exit; cleanup fallback was necessary.' };
  if (!cleanupProven || cleanup.errors.length) fatal ??= { stage: 'cleanup', message: 'Owned process/directory cleanup was not fully proved; inspect cleanup.json.' };
  try {
    const compactActions = (items) => items.map((item) => ({ identityKey: nativeIdentityKey(item.identity), processId: item.identity.processId,
      method: item.result.method, acted: item.result.acted === true, exited: item.result.exited === true }));
    const essentialCleanup = { normalExit, cleanupProven, allFourPortsReleased: cleanup.allFourPortsReleased,
      fallbackActions: compactActions(cleanup.fallbackActions), helperReclaims: compactActions(cleanup.helperReclaims),
      identityChecks: cleanup.identityChecks.map((item) => ({ identityKey: nativeIdentityKey(item.identity), processId: item.identity.processId, exitedBeforeFallback: item.exitedBeforeFallback })),
      directories: cleanup.directories, errors: cleanup.errors };
    const terminal = await persistNativeTerminalEvidence({
      write: (name, value, options) => evidence(name, value, { ...options, terminal: true }),
      secrets: secretValues, deadline: totalDeadline,
      normalExit: { ...nativeClose, normalExit, fallbackUsedBeforeThisEvidence: cleanup.fallbackActions.length > 0 || cleanup.helperReclaims.length > 0,
        serverShutdownBoundary: 'Studio may force-terminate its managed console Server after its bounded close wait; this does not prove graceful Server shutdown or recovery.' },
      cleanup: essentialCleanup,
      result: { runId, validationSlice, scenario, evidenceParentSelection: selectedEvidence?.[0] ?? null, requestedNativeWindow: windowConfiguration,
        ...(sqlDialogsRequested ? { sqlDialogs: { requested: true, passed: sqlDialogPhases.length === 4 && sqlDialogPhases.every((phase) => phase.passed),
          phases: sqlDialogPhases.map((phase) => ({ phase: phase.phase, passed: phase.passed, requestObserved: Boolean(phase.request), responseObserved: Boolean(phase.response) })),
          inputPath: sqlDialogPaths.input, outputPath: sqlDialogPaths.output, fixtureSqlExecuted: false,
          boundary: 'SQL open/save success/cancel validation scope. Actual OS windows/actions, real bridge DTOs, normal rendered SQL/tabs and exact owned disk bytes must each be confirmed; consult per-phase passed and parent OS evidence.' },
          lifecycle: { passed: normalExit && cleanupProven && !cleanup.fallbackActions.length && !cleanup.helperReclaims.length } } : {}),
        nativeWindowObservations, nativeUiPreparation, passed: !fatal && normalExit && cleanupProven, fatal: fatal ?? null, normalExit, cleanupProven, counters,
        finishedAtUtc: new Date().toISOString(), elapsedSeconds: (Date.now() - startedAt) / 1000, evidenceRoot: runRoot,
        limitations: ['API setup/login with real auth localStorage; login UI not verified.', sqlDialogsRequested
          ? 'SQL open/save success/cancel only; no other native dialog, installation, NativeAOT or full three-host acceptance.'
          : 'No native file dialog, installation, NativeAOT or full three-host acceptance.', 'Managed Server shutdown may use the existing Studio bounded forced termination.'] },
      details: [
        { name: 'bridge-responses.json', value: () => bridgeEvidence },
        { name: 'process-events.json', value: () => compactNativeProcessEvidence({ runnerIdentity, studioIdentity, events: records, helpers, streamCounts }) },
        ...(sqlDialogsRequested && sqlDialogPhases.some((phase) => !phase.passed)
          ? [{ name: 'sql-dialog-failure.json', value: () => ({ runId, stage: fatal?.stage ?? null, phases: sqlDialogPhases }) }] : []),
      ],
    });
    if (!terminal.passed) fatal ??= terminal.result.fatal ?? { stage: 'evidence', message: 'Independent terminal evidence did not prove acceptance.' };
    console.log(`${validationSlice} terminal writes: ${JSON.stringify(terminal.outcomes)}`);
  } catch (error) { console.error(`Terminal evidence preparation failed: ${safeMessage(error)}`); fatal ??= { stage: 'evidence', message: 'Terminal evidence could not be prepared.' }; }
  process.removeListener('SIGINT', cancel);
  process.removeListener('SIGTERM', cancel);
  console.log(`${validationSlice} ${fatal ? 'FAIL' : 'PASS'}: ${runRoot}`);
  // A CDP attachment may retain a Node transport after the native host exits.
  // All native-process checks and evidence are complete before ending Node.
  process.exit(fatal ? 1 : 0);
}
