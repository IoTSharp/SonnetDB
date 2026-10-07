import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { access, lstat, mkdir, readFile, readdir, realpath, rm, rmdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';
import { discoverOwnedProcessIdentities } from '../../../web/e2e/run-workbench-real.mjs';
import { acceptOwnedIdentity, attemptIndependentSteps, captureOwnedSnapshot, ownedIdentityFailure, recordOwnedIdentityEvent,
  validateOwnedIdentityAnchor, validateOwnedIdentityLedger as validateLedger } from './query-host-evidence.mjs';

// Explicit local tools and a distinct test entry: no download, production hook or HTTP fixture.
const extensionRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(extensionRoot, '..', '..');
const evidenceParent = path.join(repositoryRoot, 'artifacts', 'wb44-validation-20261007');
const code = 'C:\\Users\\mysti\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe';
const dotnet = 'C:\\Program Files\\dotnet\\dotnet.exe';
const powershell = 'C:\\Program Files\\PowerShell\\7\\pwsh.exe';
const serverDll = path.join(repositoryRoot, 'src', 'SonnetDB', 'bin', 'Release', 'net10.0', 'SonnetDB.dll');
const hostEntry = path.join(extensionRoot, 'out', 'test', 'host', 'query-real.js');
const runId = `query-host-real-${randomUUID()}`;
const runRoot = path.join(evidenceParent, runId);
const runtimeRoot = path.join(runRoot, 'runtime');
const contentRoot = path.join(runtimeRoot, 'server-content');
const dataRoot = path.join(contentRoot, 'data');
const profileRoot = path.join(runtimeRoot, 'code-profile');
const baseUrl = 'http://127.0.0.1:18346';
const database = 'Workbench44';
const label = 'WB44 isolated real Server';
const table = 'DeviceID_Main';
const startedAtUtc = new Date().toISOString();
const deadline = Date.now() + 600_000;
const controller = new AbortController();
const identities = new Map();
const roots = [];
const events = [];
const helperStarts = [];
const secrets = [];
const outputSummaries = [];
const helperOutputSummaries = [];
const outputRecords = [];
const auditFailures = [];
const cleanupAttempts = [];
const terminalAttempts = [];
let portsReleased = false;
let helperCleanupProven = false;
let outputComplete = false;
let auditFailureOverflow = false;
let auditFailureCount = 0;
const maximumFiles = 24;
const maximumFileBytes = 512 * 1024;
const maximumTotalBytes = 8 * 1024 * 1024;
let filesWritten = 0;
let bytesWritten = 0;
let snapshots = 0;
let helpers = 0;
let auditTimer;
let pendingAudit;
let runtimeCreated = false;
let processCleanupProven = false;
let runtimeRemoved = false;
let outcome = 'FAIL';
let stage = 'preflight';
let codeExit = null;
let hostOutcome = null;
let referenceHash;
let serverHash;
let finishedAtUtc;
let primaryFailure = false;
let failureType = null;
let knownFailureReason = null;
let lastControlRequest = null;
// Reserve ninety seconds inside the six-hundred-second run cap for verified cleanup and terminals.
const timeout = setTimeout(() => controller.abort(new Error('WB44 active run deadline.')), 510_000);
const cancel = () => controller.abort(new Error('WB44 cancelled.'));
process.on('SIGINT', cancel);
process.on('SIGTERM', cancel);

try {
  assert.equal(process.platform, 'win32');
  assert.equal(repositoryRoot.toLowerCase(), 'd:\\source\\sonnetdb');
  assert.equal(process.execPath.toLowerCase(), 'c:\\program files\\nodejs\\node.exe');
  assert.equal((await realpath(evidenceParent)).toLowerCase(), evidenceParent.toLowerCase());
  assert.equal((await lstat(evidenceParent)).isSymbolicLink(), false);
  for (const tool of [code, dotnet, powershell, serverDll, hostEntry]) await access(tool);
  const expectedServerHash = process.env.SONNETDB_QUERY_REAL_SERVER_SHA256;
  assert.match(expectedServerHash ?? '', /^[A-Fa-f0-9]{64}$/u);
  serverHash = hash(await readFile(serverDll));
  assert.equal(serverHash, expectedServerHash.toUpperCase());
  await mkdir(runRoot);
  await evidence('run.json', { schema: 'sonnetdb.wb42.run.v1', slice: 'WB44', runId, runnerPid: process.pid,
    startedAtUtc, deadlineUtc: new Date(deadline).toISOString(), code, dotnet, powershell, serverDll, serverHash,
    hostEntry, baseUrl, reservedPorts: [18346, 18347], runtimeRoot, contentRoot, dataRoot, profileRoot,
    limits: { runMilliseconds: 600_000, codeMilliseconds: 120_000, commandMilliseconds: 20_000,
      controlRequestMilliseconds: 10_000, readinessAttempts: 120, readinessMilliseconds: 60_000,
      evidenceFiles: maximumFiles, evidenceBytesPerFile: maximumFileBytes, evidenceTotalBytes: maximumTotalBytes },
    boundaries: { promptDriverOnly: true, generatedPayloadOnly: true, productionFetchCancellationVerified: false,
      renderedWebview: false, pagination: false, notebook: false, languageServer: false, installation: false } });
  const first = await snapshot();
  const self = first.find((item) => item.pid === process.pid);
  assert.ok(self?.commandLine && self.created);
  self.parentChain = parentChain(self, first);
  acceptIdentity(self, 'runner-start');
  audit(first);
  assert.deepEqual(await listeningPorts(), []);
  await mkdir(runtimeRoot);
  runtimeCreated = true;
  await writeFile(path.join(runtimeRoot, '.wb42-owner.json'), JSON.stringify({ runId, runnerPid: process.pid,
    slice: 'WB44', runtimeRoot, contentRoot, dataRoot, profileRoot }), { flag: 'wx' });
  await mkdir(contentRoot);
  await mkdir(path.join(profileRoot, 'User'), { recursive: true });
  await mkdir(path.join(runtimeRoot, 'code-extensions'));
  await writeFile(path.join(profileRoot, 'User', 'settings.json'), JSON.stringify({
    'sonnetdb.languageServer.enabled': false, 'update.mode': 'none', 'telemetry.telemetryLevel': 'off',
    'workbench.enableExperiments': false, 'extensions.autoCheckUpdates': false, 'extensions.autoUpdate': false,
  }), { flag: 'wx' });
  await writeFile(path.join(contentRoot, 'appsettings.json'), JSON.stringify({
    Logging: { LogLevel: { Default: 'Warning', 'Microsoft.AspNetCore': 'Warning' } }, AllowedHosts: '127.0.0.1',
    Kestrel: { Endpoints: { Http: { Url: baseUrl, Protocols: 'Http1' },
      FrameH2: { Url: 'http://127.0.0.1:18347', Protocols: 'Http2' } } },
    SonnetDBServer: { DataRoot: dataRoot, AutoLoadExistingDatabases: true, AllowAnonymousProbes: true, Tokens: {},
      Mqtt: { Enabled: false, Sparkplug: { Enabled: false }, ExternalClient: { Enabled: false } },
      Coap: { Enabled: false, Dtls: { Enabled: false } }, LineProtocolUdp: { Enabled: false },
      Modbus: { Enabled: false }, SemanticSearch: { Enabled: false } },
  }), { flag: 'wx' });
  stage = 'server-start';
  const environment = isolatedEnvironment();
  const server = await start(dotnet, [serverDll, '--contentRoot', contentRoot, '--environment', 'Production'],
    { ...environment, DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production' }, 'server');
  auditTimer = setInterval(() => {
    if (pendingAudit || controller.signal.aborted) return;
    pendingAudit = snapshot().then(audit).catch(() => { primaryFailure = true; controller.abort(new Error('WB44 process audit failed.')); })
      .finally(() => { pendingAudit = undefined; });
  }, 4_000);
  stage = 'server-readiness';
  const readyDeadline = Date.now() + 60_000;
  let ready = false;
  for (let attempt = 0; attempt < 120 && Date.now() < readyDeadline; attempt += 1) {
    controller.signal.throwIfAborted();
    assert.equal(server.child.exitCode, null);
    assert.equal(server.child.signalCode, null);
    try { const response = await fetch(`${baseUrl}/healthz/ready`, { signal: AbortSignal.any([controller.signal, AbortSignal.timeout(1_000)]) });
      ready = response.ok; await response.body?.cancel(); } catch { controller.signal.throwIfAborted(); }
    if (ready) break;
    if (attempt > 0 && attempt % 20 === 0) console.log(`WB44 real Server readiness ${attempt}/120.`);
    await delay(500, undefined, { signal: controller.signal });
  }
  assert.equal(ready, true);

  stage = 'prepare-real-server';
  const password = `WB44_${randomBytes(24).toString('hex')}!`;
  const administratorBearer = `wb44_${randomBytes(32).toString('hex')}`;
  rememberSecret(password); rememberSecret(administratorBearer);
  const setup = await jsonRequest('GET', '/v1/setup/status');
  assert.equal(setup.needsSetup, true);
  assert.equal(typeof setup.suggestedServerId, 'string');
  const administrator = await jsonRequest('POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB44 isolated real Extension Host',
    username: 'wb44_admin', password, bearerToken: administratorBearer,
  }, undefined, 201);
  assert.equal(administrator.isSuperuser, true);
  assert.equal(typeof administrator.token, 'string');
  rememberSecret(administrator.token);
  await jsonRequest('POST', '/v1/db', { name: database }, administrator.token, 201);
  await sqlRequest('/v1/sql', `CREATE USER wb44_reader WITH PASSWORD '${password}'`, administrator.token);
  await sqlRequest('/v1/sql', `GRANT READ ON DATABASE ${database} TO wb44_reader`, administrator.token);
  const reader = await jsonRequest('POST', '/v1/auth/login', { username: 'wb44_reader', password });
  assert.equal(reader.isSuperuser, false);
  assert.equal(typeof reader.token, 'string');
  rememberSecret(reader.token);
  const sqlPath = `/v1/db/${database}/sql`;
  await sqlRequest(sqlPath, `CREATE TABLE "${table}" ("DeviceID" INT, "MixedCaseName" STRING, PRIMARY KEY ("DeviceID"))`, administrator.token);
  const seed = [[1, 'WB44:first'], [2, 'WB44:second'], [3, 'WB44:third'], [4, 'WB44:fourth'], [5, 'WB44:fifth']];
  const insert = await sqlRequest(sqlPath, `INSERT INTO "${table}" ("DeviceID", "MixedCaseName") VALUES ${seed.map((row) => `(${row[0]}, '${row[1]}')`).join(', ')}`, administrator.token);
  assert.equal(insert.result.end?.recordsAffected, 5);
  const count = await sqlRequest(sqlPath, `SELECT COUNT(*) FROM "${table}"`, administrator.token);
  assert.deepEqual(count.result.rows, [[5]]);
  const schema = await jsonRequest('GET', `/v1/db/${database}/schema`, undefined, administrator.token);
  assert.equal(schema.tables?.length, 1);
  assert.equal(schema.tables[0].name, table);
  assert.deepEqual(schema.tables[0].columns.map((column) => column.name), ['DeviceID', 'MixedCaseName']);
  const selectedSql = `SELECT "DeviceID", "MixedCaseName" FROM "${table}" WHERE "DeviceID" = 2;`;
  const currentSql = `SELECT "DeviceID", "MixedCaseName" FROM "${table}" WHERE "DeviceID" >= 4 ORDER BY "DeviceID" LIMIT 2;`;
  const explainSql = `EXPLAIN ${currentSql.replace(/;+\s*$/u, '')};`;
  const document = `${selectedSql}\n\n${currentSql}\n`;
  const phases = [
    { name: 'current-statement', command: 'sonnetdb.runQuery', document, start: document.indexOf(currentSql) + 12, end: document.indexOf(currentSql) + 12, sql: currentSql },
    { name: 'exact-selection', command: 'sonnetdb.runSelection', document, start: 0, end: selectedSql.length, sql: selectedSql },
    { name: 'explain', command: 'sonnetdb.explainQuery', document: currentSql, start: 12, end: 12, sql: explainSql },
  ];
  const references = [];
  for (let index = 0; index < phases.length && index < 3; index += 1) {
    const actual = await sqlRequest(sqlPath, phases[index].sql, administrator.token);
    if (index === 0) assert.deepEqual(actual.result.rows, seed.slice(3));
    if (index === 1) assert.deepEqual(actual.result.rows, [seed[1]]);
    assert.ok(actual.result.rows.length > 0 && actual.result.rows.length <= 100);
    references.push({ ...phases[index], result: actual.result, actualReferenceResponse: actual.response });
  }
  const reference = { schema: 'sonnetdb.wb42.reference.v1', slice: 'WB44', runId, baseUrl, database, label,
    previewRequestShape: 'sql-only', previewContract: { requestBodyKeys: ['sql'], previewMaxRowsSent: false, rowLimit: 100,
      source: 'diagnostic-admission', serverFullResult: true },
    seedCount: 5, columns: ['DeviceID', 'MixedCaseName'], nonSuperuserReadOnly: true, phases: references };
  referenceHash = await evidence('reference.json', reference);

  stage = 'extension-host';
  const codeArguments = [`--user-data-dir=${profileRoot}`, `--extensions-dir=${path.join(runtimeRoot, 'code-extensions')}`,
    `--extensionDevelopmentPath=${extensionRoot}`, `--extensionTestsPath=${hostEntry}`, '--disable-extensions', '--disable-gpu',
    '--disable-updates', '--disable-workspace-trust', '--no-cached-data', '--skip-release-notes', '--skip-welcome', '--new-window', '--wait', extensionRoot];
  const codeRecord = await start(code, codeArguments, { ...environment, SONNETDB_QUERY_REAL_RUN_ROOT: runRoot,
    SONNETDB_QUERY_REAL_TOKEN: reader.token, SONNETDB_QUERY_REAL_REFERENCE_SHA256: referenceHash }, 'code');
  codeExit = await waitExit(codeRecord, 120_000);
  assert.deepEqual(codeExit, { code: 0, signal: null });
  const hostResult = JSON.parse(await readFile(path.join(runRoot, 'host-result.json'), 'utf8'));
  hostOutcome = hostResult.outcome;
  assert.equal(hostOutcome, 'PASS');
  assert.deepEqual(hostResult.completed, ['current-statement', 'exact-selection', 'explain']);
  assert.equal(hostResult.apiRestored, true);
  assert.equal(hostResult.productionFetchTimeoutVerified, false);
  outcome = 'PASS';
} catch (error) {
  primaryFailure = true;
  outcome = 'FAIL';
  failureType = error instanceof Error && ['AssertionError', 'TypeError', 'Error'].includes(error.name) ? error.name : 'unclassified';
  const reasons = new Map([['Owned helper deadline.', 'helper_deadline'], ['Code test deadline.', 'code_deadline'],
    ['WB44 active run deadline.', 'active_deadline'], ['WB44 process audit failed.', 'process_audit'],
    ['WB44 cancelled.', 'cancelled'], ['Owned output byte limit exceeded.', 'output_budget']]);
  knownFailureReason = reasons.get(error instanceof Error ? error.message : '') ?? (failureType === 'AssertionError' ? 'assertion' : 'unclassified');
  console.error(`WB44 failed during ${stage}; raw error/child output omitted to protect runtime credentials.`);
} finally {
  clearInterval(auditTimer);
  if (pendingAudit) { try { await pendingAudit; } catch { primaryFailure = true; } }
  stage = primaryFailure ? stage : 'cleanup';
  cleanupAttempts.push(...await attemptIndependentSteps([{ name: 'owned-processes', run: async () => {
    const cleanupDeadline = Math.min(deadline - 35_000, Date.now() + 45_000);
    for (let round = 0; round < 3 && Date.now() < cleanupDeadline; round += 1) {
      let current;
      try { current = await snapshot(true); }
      catch { noteAuditFailure({ stage: 'cleanup-snapshot', reason: 'snapshot_failed' }); }
      if (current) { try { audit(current); } catch { /* Accepted identities remain available for cleanup. */ } }
      const live = (current ? safeLiveIdentities(current) : [...identities.values()]).filter((item) => item.pid !== process.pid
        && !helperStarts.some((helperRecord) => helperRecord.pid === item.pid && helperRecord.closed))
        .sort((left, right) => (right.parentChain?.length ?? 0) - (left.parentChain?.length ?? 0));
      if (live.length === 0) break;
      assert.ok(live.length <= 128);
      for (let index = 0; index < live.length && index < 128; index += 1) {
        assert.ok(Date.now() < cleanupDeadline);
        try { await stopVerified(live[index]); }
        catch { noteAuditFailure({ stage: 'stop', pid: live[index].pid, reason: 'ownership_or_verifier_failed_process_preserved' }); }
      }
      await delay(100);
    }
    const final = await snapshot(true);
    try { audit(final); } catch { /* A failed audit must not hide the independently observed terminal processes. */ }
    const remaining = safeLiveIdentities(final).filter((item) => item.pid !== process.pid);
    const verifiedRemaining = remaining.filter((item) => !helperStarts.some((helper) => helper.pid === item.pid && helper.closed));
    assert.deepEqual(verifiedRemaining, []);
    assert.ok(roots.every((root) => root.identity));
    assert.equal(auditFailureOverflow || auditFailures.some((failure) => failure.stage !== 'event'), false);
    processCleanupProven = true;
  } }, { name: 'reserved-ports', run: async () => {
    assert.deepEqual(await listeningPorts(), []); portsReleased = true;
  } }, { name: 'helper-handles', run: async () => {
    assert.ok(helperStarts.every((helperRecord) => helperRecord.closed && helperRecord.identityRecorded));
    helperCleanupProven = true;
  } }, { name: 'owned-runtime', run: async () => {
    assert.equal(processCleanupProven && portsReleased && helperCleanupProven, true);
    if (runtimeCreated) { await removeRuntime(); runtimeRemoved = true; }
    else runtimeRemoved = true;
  } }, { name: 'bounded-output-hashes', run: finishOutputs }], { deadline: deadline - 15_000, maximumSteps: 5 }));
  clearTimeout(timeout);
  process.removeListener('SIGINT', cancel);
  process.removeListener('SIGTERM', cancel);
  finishedAtUtc = new Date().toISOString();
  if (primaryFailure || auditFailureOverflow || auditFailures.length || cleanupAttempts.some((attempt) => !attempt.ok)
    || !processCleanupProven || !runtimeRemoved || !outputComplete || !helperCleanupProven || !portsReleased) outcome = 'FAIL';
  const terminalSteps = [{ name: 'process-events.json', run: async () => {
    await evidence('process-events.json', { schema: 'sonnetdb.wb42.process-events.v1', slice: 'WB44', runId, events,
      acceptedIdentities: [...identities.values()], auditFailures, auditFailureCount, auditFailureOverflow, identityLedgerIsAuthoritative: true,
      ownershipAnchorPid: process.pid, externalAncestorsDiagnosticOnly: true,
      trackedIdentities: identities.size, helperStarts: helperStarts.map(({ pid, startedAtUtc: time, closed, identityRecorded }) => ({ pid, startedAtUtc: time, closed, identityRecorded })),
      snapshots, helpers, finishedAtUtc });
  } }, { name: 'child-output.json', run: async () => {
    await evidence('child-output.json', { schema: 'sonnetdb.wb42.child-output.v1', slice: 'WB44', runId, streams: outputSummaries,
      helperStreams: helperOutputSummaries, outputComplete,
      rawOutputRetained: false, reason: 'Only byte counts/hashes are retained; output can contain runtime credentials.' });
  } }, { name: 'cleanup.json', run: async () => {
    await evidence('cleanup.json', { schema: 'sonnetdb.wb42.cleanup.v1', slice: 'WB44', runId, processCleanupProven,
      runtimeCreated, runtimeRemoved, runtimeRoot, ports: [18346, 18347], portsReleased, helperCleanupProven, outputComplete, attempts: cleanupAttempts, finishedAtUtc });
  } }, { name: 'result.json', run: async () => {
    await evidence('result.json', { schema: 'sonnetdb.wb42.result.v1', slice: 'WB44', runId, outcome, primaryFailure, stoppedAtStage: stage,
      failureType, knownFailureReason, lastControlRequest,
      codeExit, normalCodeExitVerified: codeExit?.code === 0 && codeExit?.signal === null, hostOutcome, referenceHash, serverHash,
      processCleanupProven, runtimeRemoved, portsReleased, helperCleanupProven, outputComplete,
      terminalEvidenceRequiresStatus: true, startedAtUtc, finishedAtUtc });
  } }, { name: 'manifest.json', run: manifest }];
  // Each writer has an independent attempt; no failed detail writer can suppress cleanup/result.
  for (let index = 0; index < terminalSteps.length && index < 5; index += 1) {
    const attempts = await attemptIndependentSteps([terminalSteps[index]], { deadline: deadline - 5_000, maximumSteps: 1 });
    terminalAttempts.push(...attempts);
    if (attempts.some((attempt) => !attempt.ok)) outcome = 'FAIL';
  }
  try { await evidence('terminal-status.json', { schema: 'sonnetdb.wb42.terminal-status.v1', slice: 'WB44', runId,
    outcome, attempts: terminalAttempts, manifestSha256: await existingHash('manifest.json'),
    resultSha256: await existingHash('result.json'), statusExcludedFromManifest: true, finishedAtUtc }); }
  catch { outcome = 'FAIL'; console.error('WB44 final status write failed; terminal evidence is incomplete.'); }
  process.exitCode = outcome === 'PASS' ? 0 : 1;
  console.log(`WB44 ${outcome}: ${runRoot}`);
}

function hash(value) { return createHash('sha256').update(value).digest('hex').toUpperCase(); }
function rememberSecret(value) {
  assert.equal(typeof value, 'string'); assert.ok(value.length >= 24);
  for (const variant of [value, Buffer.from(value).toString('base64'), Buffer.from(value).toString('base64url'),
    Buffer.from(value).toString('hex'), Buffer.from(value).toString('hex').toUpperCase(), encodeURIComponent(value)]) {
    if (!secrets.includes(variant)) secrets.push(variant);
  }
}
function safeText(text) {
  for (const secret of secrets) assert.equal(text.includes(secret), false, 'Runtime credentials cannot enter evidence.');
  assert.equal(/Bearer\s+[A-Za-z0-9._~+/=-]{16,}/iu.test(text), false);
}
async function evidence(name, value) {
  assert.match(name, /^(run|reference|process-events|child-output|cleanup|result|manifest|terminal-status)\.json$/u);
  const text = `${JSON.stringify(value, null, 2)}\n`; safeText(text);
  const size = Buffer.byteLength(text);
  assert.ok(size <= maximumFileBytes && ++filesWritten <= maximumFiles && (bytesWritten += size) <= maximumTotalBytes);
  await writeFile(path.join(runRoot, name), text, { flag: 'wx' });
  return hash(text);
}
function event(value) {
  recordOwnedIdentityEvent(value, identities, events, { validateText: safeText });
}
function noteAuditFailure(value) {
  primaryFailure = true;
  auditFailureCount += 1;
  if (auditFailures.length < 128) auditFailures.push({ atUtc: new Date().toISOString(), ...value });
  else auditFailureOverflow = true;
}
function acceptIdentity(identity, eventName, extra = {}) {
  try {
    return acceptOwnedIdentity(identity, identities, { ownerPid: process.pid, validateText: safeText, validateLedger,
      eventName, recordEvent: (value) => event({ ...value, ...extra }) });
  } catch (error) {
    noteAuditFailure(ownedIdentityFailure(error, identity.pid));
    throw error;
  }
}
function isolatedEnvironment() {
  const environment = { ...process.env }; const names = Object.keys(environment); const expires = Date.now() + 1_000;
  assert.ok(names.length <= 2048);
  for (let index = 0; index < names.length && index < 2048; index += 1) {
    assert.ok(Date.now() < expires);
    if (/^(SONNETDB_|ASPNETCORE_|DOTNET_ENVIRONMENT$|SonnetDBServer(?:__|:)|Kestrel(?:__|:)|ConnectionStrings(?:__|:)|URLS$|ELECTRON_RUN_AS_NODE$|VSCODE_)/iu.test(names[index])) delete environment[names[index]];
  }
  return environment;
}
function parentChain(identity, snapshotValue) {
  const chain = []; let parentPid = identity.parentPid; const expires = Date.now() + 1_000;
  for (let depth = 0; depth < 12 && parentPid > 0 && Date.now() < expires; depth += 1) {
    const parent = identities.get(parentPid) ?? snapshotValue.find((item) => item.pid === parentPid);
    if (!parent) { chain.push({ pid: parentPid, unavailable: true }); break; }
    chain.push({ pid: parent.pid, parentPid: parent.parentPid, created: parent.created, commandLine: parent.commandLine });
    if (chain.some((item) => item.pid === parent.parentPid)) break;
    parentPid = parent.parentPid;
  }
  return chain;
}
function audit(snapshotValue) {
  const failures = captureOwnedSnapshot(snapshotValue, identities, roots, { discover: discoverOwnedProcessIdentities,
    ownerPid: process.pid, validateText: safeText, validateLedger, recordEvent: event });
  for (let index = 0; index < failures.length && index < 128; index += 1) noteAuditFailure(failures[index]);
  if (failures.length) throw new Error('WB44 process audit failed.');
}
function safeLiveIdentities(snapshotValue) {
  assert.ok(snapshotValue.length <= 4096 && identities.size <= 128);
  const live = []; const expires = Date.now() + 3_000;
  let count = 0;
  for (const identity of identities.values()) {
    assert.ok(++count <= 128 && Date.now() < expires);
    const current = snapshotValue.find((item) => item.pid === identity.pid);
    if (!current) continue;
    if (current.created !== identity.created || current.parentPid !== identity.parentPid || current.commandLine !== identity.commandLine) {
      noteAuditFailure({ stage: 'identity', pid: identity.pid, reason: 'recorded_identity_changed_replacement_preserved' });
      continue;
    }
    live.push(identity);
  }
  return live;
}
async function start(executable, args, environment, role) {
  controller.signal.throwIfAborted(); assert.ok(roots.length < 2);
  const child = spawn(executable, args, { cwd: extensionRoot, env: environment, windowsHide: role !== 'code', stdio: ['ignore', 'pipe', 'pipe'] });
  const record = { child, pid: child.pid, startedAtUtc: new Date().toISOString(), exitedAtUtc: null, identity: null, role };
  roots.push(record);
  child.once('exit', () => { record.exitedAtUtc = new Date().toISOString(); });
  for (const [streamName, stream] of [['stdout', child.stdout], ['stderr', child.stderr]]) {
    const digest = createHash('sha256'); let bytes = 0; let hashedBytes = 0; let overflow = false;
    const output = { role, stream: streamName, handle: stream, ended: false, closed: false, failed: false, finalized: false };
    const onData = (chunk) => {
      if (output.finalized) return;
      bytes += chunk.length;
      const kept = chunk.subarray(0, Math.max(0, maximumFileBytes - hashedBytes));
      digest.update(kept); hashedBytes += kept.length;
      if (bytes > maximumFileBytes) { overflow = true; primaryFailure = true; controller.abort(new Error('Owned output byte limit exceeded.')); }
    };
    stream.on('data', onData);
    stream.once('end', () => { output.ended = true; });
    stream.once('close', () => { output.closed = true; });
    stream.once('error', () => { output.failed = true; primaryFailure = true; });
    output.finalize = () => {
      if (output.finalized) return;
      output.finalized = true; stream.removeListener('data', onData);
      const complete = output.ended && !output.failed && !overflow;
      outputSummaries.push({ role, stream: streamName, bytes, hashedBytes, overflow, ended: output.ended,
        closed: output.closed, complete, hashScope: complete ? 'complete-output' : 'bounded-observed-prefix',
        sha256OfBoundedOutput: digest.digest('hex').toUpperCase() });
      stream.destroy();
    };
    outputRecords.push(output);
  }
  await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
  const current = await snapshot();
  const identity = current.find((item) => item.pid === record.pid);
  assert.ok(identity?.commandLine && identity.parentPid === process.pid);
  identity.parentChain = parentChain(identity, current);
  record.identity = acceptIdentity(identity, 'owned-root-start', { role, command: [executable, ...args] });
  audit(current);
  return record;
}
async function waitExit(record, milliseconds) {
  if (record.child.exitCode !== null || record.child.signalCode !== null) return { code: record.child.exitCode, signal: record.child.signalCode };
  controller.signal.throwIfAborted();
  return await new Promise((resolve, reject) => {
    let timer;
    const cleanup = () => { clearTimeout(timer); record.child.removeListener('exit', done);
      record.child.removeListener('error', failed); controller.signal.removeEventListener('abort', cancelled); };
    const done = (exitCode, signal) => { cleanup(); resolve({ code: exitCode, signal }); };
    const failed = (error) => { cleanup(); reject(error); };
    const cancelled = () => failed(controller.signal.reason);
    record.child.once('exit', done); record.child.once('error', failed);
    controller.signal.addEventListener('abort', cancelled, { once: true });
    timer = setTimeout(() => failed(new Error('Code test deadline.')), milliseconds);
  });
}
async function jsonRequest(method, route, data, bearer, expectedStatus = 200) {
  lastControlRequest = { method, route, status: null };
  const response = await fetch(`${baseUrl}${route}`, { method, headers: { ...(data ? { 'Content-Type': 'application/json' } : {}),
    ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}) }, ...(data ? { body: JSON.stringify(data) } : {}),
    signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]) });
  lastControlRequest.status = response.status;
  assert.equal(response.status, expectedStatus);
  const raw = await response.text(); assert.ok(Buffer.byteLength(raw) <= maximumFileBytes);
  return JSON.parse(raw);
}
async function sqlRequest(route, sql, bearer) {
  lastControlRequest = { method: 'POST', route, status: null };
  const response = await fetch(`${baseUrl}${route}`, { method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${bearer}` },
    body: JSON.stringify({ sql }), signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10_000)]) });
  lastControlRequest.status = response.status;
  assert.equal(response.status, 200); const contentType = response.headers.get('content-type'); assert.ok(contentType?.includes('ndjson'));
  const raw = await response.text(); assert.ok(Buffer.byteLength(raw) <= maximumFileBytes);
  const lines = raw.trim().split(/\r?\n/u); assert.ok(lines.length <= 128); const parseDeadline = Date.now() + 2_000;
  const result = { columns: [], rows: [], end: null, error: null, hasColumns: false }; let ends = 0;
  for (let index = 0; index < lines.length && index < 128; index += 1) {
    assert.ok(Date.now() < parseDeadline);
    const frame = JSON.parse(lines[index]);
    if (Array.isArray(frame)) result.rows.push(frame);
    else if (frame.type === 'meta') { result.columns = frame.columns; result.hasColumns = true; }
    else if (frame.type === 'end') { result.end = frame; ends += 1; }
    else if (frame.type === 'error') result.error = frame;
  }
  assert.equal(result.error, null); assert.equal(ends, 1); assert.ok(result.rows.length <= 100);
  return { result, response: { status: response.status, contentType, bytes: Buffer.byteLength(raw), sha256: hash(raw),
    requestBody: { sql }, previewRequestShape: 'sql-only', frames: lines.map((line) => JSON.parse(line)) } };
}
async function finishOutputs() {
  const expires = Math.min(deadline - 15_000, Date.now() + 10_000);
  for (let attempt = 0; attempt < 100 && Date.now() < expires; attempt += 1) {
    if (outputRecords.every((record) => record.ended || record.closed || record.failed)) break;
    await delay(100);
  }
  for (let index = 0; index < outputRecords.length && index < 4; index += 1) outputRecords[index].finalize();
  for (let index = 0; index < roots.length && index < 2; index += 1) roots[index].child.unref();
  outputComplete = outputRecords.length === roots.length * 2 && outputSummaries.length === outputRecords.length
    && outputSummaries.every((summary) => summary.complete) && helperOutputSummaries.length === helperStarts.length * 2
    && helperOutputSummaries.every((summary) => summary.complete);
  assert.equal(outputComplete, true);
}
async function existingHash(name) {
  try { const raw = await readFile(path.join(runRoot, name)); assert.ok(raw.length <= maximumFileBytes); return hash(raw); }
  catch { return null; }
}

async function snapshot(final = false) {
  assert.ok(++snapshots <= 80 && (final || Date.now() < deadline - 90_000));
  const snapshotScript = `$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$taskProcesses = @($taskHelperSnapshot)
if ($taskProcesses.Count -gt 4096) { throw 'Snapshot count exceeded.' }
$taskDeadline = [DateTime]::UtcNow.AddSeconds(3)
$taskRecords = [Collections.Generic.List[object]]::new()
for ($taskIndex = 0; $taskIndex -lt $taskProcesses.Count; $taskIndex++) {
 if ([DateTime]::UtcNow -ge $taskDeadline) { throw 'Snapshot wall clock exceeded.' }
 $taskProcess = $taskProcesses[$taskIndex]
 if ($null -eq $taskProcess.CreationDate) { continue }
 $taskRecords.Add([ordered]@{ pid=[int]$taskProcess.ProcessId; parentPid=[int]$taskProcess.ParentProcessId;
   created=$taskProcess.CreationDate.ToUniversalTime().ToString('O'); commandLine=$taskProcess.CommandLine })
}
ConvertTo-Json -InputObject ($taskRecords.ToArray()) -Compress -Depth 4`;
  const values = await helper(snapshotScript);
  assert.ok(Array.isArray(values) && values.length <= 4096);
  return values;
}
async function listeningPorts() {
  return await helper(`$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$taskPorts = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @(18346,18347) } | Select-Object -First 5 -ExpandProperty LocalPort)
ConvertTo-Json -InputObject $taskPorts -Compress`);
}
async function helper(script, input) {
  assert.ok(++helpers <= 112);
  const wrapped = `$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required.' }
$taskHelperSnapshot = @(Get-CimInstance Win32_Process -OperationTimeoutSec 3 | Select-Object -First 4097)
if ($taskHelperSnapshot.Count -gt 4096) { throw 'Helper snapshot count exceeded.' }
$taskHelperLookup = @{}
$taskHelperLookupDeadline = [DateTime]::UtcNow.AddSeconds(3)
for ($taskHelperIndex = 0; $taskHelperIndex -lt $taskHelperSnapshot.Count -and $taskHelperIndex -lt 4096; $taskHelperIndex++) {
 if ([DateTime]::UtcNow -ge $taskHelperLookupDeadline) { throw 'Helper lookup wall clock exceeded.' }
 $taskHelperProcess = $taskHelperSnapshot[$taskHelperIndex]
 $taskHelperLookup[[int]$taskHelperProcess.ProcessId] = $taskHelperProcess
}
$taskSelf = $taskHelperLookup[[int]$PID]
if ($null -eq $taskSelf -or $null -eq $taskSelf.CreationDate -or [string]::IsNullOrEmpty($taskSelf.CommandLine)) { throw 'Helper self identity incomplete.' }
$taskIdentity = [ordered]@{pid=[int]$taskSelf.ProcessId; parentPid=[int]$taskSelf.ParentProcessId; created=$taskSelf.CreationDate.ToUniversalTime().ToString('O'); commandLine=$taskSelf.CommandLine}
$taskChain = [Collections.Generic.List[object]]::new()
$taskParentPid = [int]$taskSelf.ParentProcessId
$taskChainDeadline = [DateTime]::UtcNow.AddSeconds(2)
for ($taskDepth = 0; $taskDepth -lt 12 -and $taskParentPid -gt 0; $taskDepth++) {
 if ([DateTime]::UtcNow -ge $taskChainDeadline) { throw 'Helper parent-chain deadline.' }
 $taskParent = $taskHelperLookup[[int]$taskParentPid]
 if ($null -eq $taskParent) { $taskChain.Add(@{pid=$taskParentPid;unavailable=$true}); break }
 $taskChain.Add(@{pid=[int]$taskParent.ProcessId;parentPid=[int]$taskParent.ParentProcessId;created=$taskParent.CreationDate.ToUniversalTime().ToString('O');commandLine=$taskParent.CommandLine})
 $taskParentPid = [int]$taskParent.ParentProcessId
}
$taskIdentity.parentChain = $taskChain.ToArray()
[Console]::Out.WriteLine((ConvertTo-Json -InputObject $taskIdentity -Compress -Depth 5))
${script}`;
  const child = spawn(powershell, ['-NoProfile', '-Command', wrapped], { cwd: extensionRoot, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
  const record = { pid: child.pid, startedAtUtc: new Date().toISOString(), closed: false, identityRecorded: false };
  helperStarts.push(record);
  if (input) child.stdin.end(`${JSON.stringify(input)}\n`); else child.stdin.end();
  let output = ''; let exceeded = false; let identity;
  const helperStreams = [];
  for (const [streamName, stream] of [['stdout', child.stdout], ['stderr', child.stderr]]) {
    const digest = createHash('sha256'); let bytes = 0; let hashedBytes = 0; let ended = false; let failed = false;
    const cap = 4 * 1024 * 1024;
    const onData = (chunk) => {
      bytes += chunk.length;
      const kept = chunk.subarray(0, Math.max(0, cap - hashedBytes)); digest.update(kept); hashedBytes += kept.length;
      if (bytes > cap) exceeded = true;
    };
    stream.on('data', onData);
    stream.once('end', () => { ended = true; });
    stream.once('error', () => { failed = true; });
    helperStreams.push(() => {
      stream.removeListener('data', onData);
      helperOutputSummaries.push({ role: 'helper', pid: record.pid, stream: streamName, bytes, hashedBytes,
        complete: ended && !failed && bytes <= cap, hashScope: ended && !failed && bytes <= cap ? 'complete-output' : 'bounded-observed-prefix',
        sha256OfBoundedOutput: digest.digest('hex').toUpperCase() });
      stream.destroy();
    });
  }
  child.stdout.on('data', (chunk) => {
    if (Buffer.byteLength(output) + chunk.length > 4 * 1024 * 1024) { exceeded = true; return; }
    output += chunk.toString('utf8');
    if (!identity && output.includes('\n')) {
      try {
        identity = JSON.parse(output.slice(0, output.indexOf('\n')).trim());
        assert.equal(identity.pid, record.pid); assert.equal(identity.parentPid, process.pid); assert.ok(identity.commandLine && identity.created);
        assert.ok(identity.parentChain?.length && identity.parentChain[0].pid === process.pid && identity.parentChain[0].commandLine);
        acceptIdentity(identity, 'owned-helper-start'); record.identityRecorded = true;
      } catch {
        const accepted = identities.get(record.pid);
        record.identityRecorded = Boolean(accepted && accepted.pid === identity?.pid && accepted.created === identity.created
          && accepted.parentPid === identity.parentPid && accepted.commandLine === identity.commandLine);
        exceeded = true;
      }
    }
  });
  child.stderr.resume();
  let timer;
  const closed = new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (exitCode, signal) => { record.closed = true; resolve({ exitCode, signal }); });
  });
  try {
    const terminal = await Promise.race([closed, new Promise((_resolve, reject) => { timer = setTimeout(() => reject(new Error('Owned helper deadline.')), 6_000); })]);
    assert.equal(terminal.exitCode, 0); assert.equal(terminal.signal, null); assert.equal(exceeded, false); assert.ok(record.identityRecorded);
    const end = output.indexOf('\n'); const body = output.slice(end + 1).trim();
    return body ? JSON.parse(body) : null;
  } finally {
    clearTimeout(timer);
    for (let index = 0; index < helperStreams.length && index < 2; index += 1) helperStreams[index]();
    child.stdin.destroy();
    if (!record.closed) child.unref();
  }
}
async function stopVerified(identity) {
  assert.notEqual(identity.pid, process.pid);
  const ownerIdentity = identities.get(process.pid);
  assert.ok(ownerIdentity);
  validateOwnedIdentityAnchor(identity, ownerIdentity);
  const ownershipAnchorIdentity = { pid: ownerIdentity.pid, parentPid: ownerIdentity.parentPid,
    created: ownerIdentity.created, commandLine: ownerIdentity.commandLine };
  const result = await helper(`$taskExpected = [Console]::In.ReadLine() | ConvertFrom-Json -DateKind String
$taskCurrent = $taskHelperLookup[[int]$taskExpected.pid]
if ($null -eq $taskCurrent) { ConvertTo-Json -InputObject @{exited=$true} -Compress; exit 0 }
if ($taskCurrent.CreationDate.ToUniversalTime().ToString('O') -ne $taskExpected.created -or
 $taskCurrent.CommandLine -cne $taskExpected.commandLine -or [int]$taskCurrent.ParentProcessId -ne [int]$taskExpected.parentPid) { throw 'Ownership changed; preserve process.' }
if ([int]$taskExpected.ownershipAnchorPid -ne [int]$taskSelf.ParentProcessId) { throw 'Ownership anchor changed; preserve process.' }
if ([int]$taskExpected.pid -eq [int]$taskExpected.ownershipAnchorPid) { throw 'Cannot stop ownership anchor.' }
$taskAnchorExpected = $taskExpected.ownershipAnchorIdentity
if ($null -eq $taskAnchorExpected -or [int]$taskAnchorExpected.pid -ne [int]$taskExpected.ownershipAnchorPid) { throw 'Ownership anchor ledger identity missing; preserve process.' }
$taskAnchorLive = $taskHelperLookup[[int]$taskAnchorExpected.pid]
if ($null -eq $taskAnchorLive -or $null -eq $taskAnchorLive.CreationDate -or [string]::IsNullOrEmpty($taskAnchorLive.CommandLine) -or
 $taskAnchorLive.CreationDate.ToUniversalTime().ToString('O') -ne $taskAnchorExpected.created -or $taskAnchorLive.CommandLine -cne $taskAnchorExpected.commandLine -or
 [int]$taskAnchorLive.ParentProcessId -ne [int]$taskAnchorExpected.parentPid) { throw 'Live ownership anchor changed or missing; preserve process.' }
$taskParentDeadline = [DateTime]::UtcNow.AddSeconds(2)
if (@($taskExpected.parentChain).Count -gt 12) { throw 'Stop parent-chain count exceeded.' }
$taskNextParentPid = [int]$taskExpected.parentPid
$taskAnchorSeen = $false
foreach ($taskParentIdentity in @($taskExpected.parentChain)) {
 if ([DateTime]::UtcNow -ge $taskParentDeadline) { throw 'Stop parent-chain wall clock exceeded.' }
 if ($taskParentIdentity.unavailable -or [int]$taskParentIdentity.pid -ne $taskNextParentPid) { throw 'Continuous ownership parent chain missing; preserve process.' }
 $taskLiveParent = $taskHelperLookup[[int]$taskParentIdentity.pid]
 if ($null -eq $taskLiveParent -or $null -eq $taskLiveParent.CreationDate -or [string]::IsNullOrEmpty($taskLiveParent.CommandLine)) { throw 'Live ownership parent missing; preserve process.' }
 if ($taskLiveParent.CreationDate.ToUniversalTime().ToString('O') -ne $taskParentIdentity.created -or $taskLiveParent.CommandLine -cne $taskParentIdentity.commandLine -or [int]$taskLiveParent.ParentProcessId -ne [int]$taskParentIdentity.parentPid) { throw 'Parent identity changed; preserve process.' }
 if ([int]$taskParentIdentity.pid -eq [int]$taskExpected.ownershipAnchorPid) {
  if ($taskParentIdentity.created -ne $taskAnchorExpected.created -or $taskParentIdentity.commandLine -cne $taskAnchorExpected.commandLine -or [int]$taskParentIdentity.parentPid -ne [int]$taskAnchorExpected.parentPid) { throw 'Parent anchor differs from ledger; preserve process.' }
  $taskAnchorSeen = $true; break
 }
 $taskNextParentPid = [int]$taskParentIdentity.parentPid
}
if (-not $taskAnchorSeen) { throw 'Live ownership anchor not reached; preserve process.' }
if ([int]$taskExpected.pid -eq $PID) { throw 'Cannot stop verifier.' }
Stop-Process -Id ([int]$taskExpected.pid) -Force -ErrorAction Stop
ConvertTo-Json -InputObject @{stopped=$true} -Compress`, { ...identity, ownershipAnchorPid: process.pid, ownershipAnchorIdentity });
  assert.ok(result.exited || result.stopped);
  event({ event: 'owned-process-stop', ...identity, alreadyExited: Boolean(result.exited) });
}
async function removeRuntime() {
  const resolved = await realpath(runtimeRoot);
  assert.equal(resolved.toLowerCase(), path.resolve(runRoot, 'runtime').toLowerCase());
  assert.equal(path.dirname(resolved).toLowerCase(), runRoot.toLowerCase());
  assert.equal((await lstat(runtimeRoot)).isSymbolicLink(), false);
  const marker = JSON.parse(await readFile(path.join(runtimeRoot, '.wb42-owner.json'), 'utf8'));
  assert.deepEqual(marker, { runId, runnerPid: process.pid, slice: 'WB44', runtimeRoot, contentRoot, dataRoot, profileRoot });
  const pending = [{ directory: resolved, depth: 0 }];
  const objects = []; const removeDeadline = Math.min(deadline - 5_000, Date.now() + 30_000);
  // Inventory first: no deletion starts if path, symlink, count or traversal bounds cannot be proved.
  for (let index = 0; index < pending.length && index < 4096; index += 1) {
    assert.ok(Date.now() < removeDeadline && pending.length <= 4096);
    const current = pending[index]; assert.ok(current.depth <= 16);
    const entries = await readdir(current.directory, { withFileTypes: true });
    assert.ok(entries.length + objects.length <= 4096);
    for (let child = 0; child < entries.length && child < 4096; child += 1) {
      assert.ok(Date.now() < removeDeadline);
      const entry = entries[child]; assert.equal(entry.isSymbolicLink(), false);
      const absolute = path.resolve(current.directory, entry.name);
      assert.ok(absolute.toLowerCase().startsWith(`${resolved.toLowerCase()}${path.sep}`));
      assert.ok(entry.isFile() || entry.isDirectory());
      objects.push({ absolute, directory: entry.isDirectory() });
      if (entry.isDirectory()) pending.push({ directory: absolute, depth: current.depth + 1 });
    }
  }
  assert.ok(pending.length <= 4096 && objects.length <= 4096);
  const markerPath = path.join(resolved, '.wb42-owner.json');
  objects.sort((left, right) => left.absolute === markerPath ? 1 : right.absolute === markerPath ? -1 : right.absolute.length - left.absolute.length);
  for (let index = 0; index < objects.length && index < 4096; index += 1) {
    assert.ok(Date.now() < removeDeadline);
    const object = objects[index]; assert.equal((await lstat(object.absolute)).isSymbolicLink(), false);
    if (object.directory) await rmdir(object.absolute); else await rm(object.absolute, { force: false, maxRetries: 0 });
  }
  await rmdir(resolved);
}
async function manifest() {
  const entries = await readdir(runRoot, { withFileTypes: true }); assert.ok(entries.length <= maximumFiles);
  const records = []; let total = 0; const expires = Date.now() + 10_000;
  for (let index = 0; index < entries.length && index < maximumFiles; index += 1) {
    assert.ok(Date.now() < expires); const entry = entries[index];
    if (entry.name === 'runtime') { assert.equal(runtimeRemoved, false); continue; }
    if (entry.name === 'terminal-status.json') continue;
    assert.ok(entry.isFile() && /^(run|reference|phase-[1-3]|observation-[1-3]|failure-observation|history|host-result|process-events|child-output|cleanup|result)\.json$/u.test(entry.name));
    const raw = await readFile(path.join(runRoot, entry.name)); assert.ok(raw.length <= maximumFileBytes); safeText(raw.toString('utf8'));
    total += raw.length; assert.ok(total <= maximumTotalBytes);
    records.push({ file: entry.name, bytes: raw.length, sha256: hash(raw) });
  }
  assert.ok(records.length < maximumFiles);
  await evidence('manifest.json', { schema: 'sonnetdb.wb42.manifest.v1', slice: 'WB44', runId, files: records,
    excludedTerminalFiles: ['manifest.json', 'terminal-status.json'],
    totalBytesWithoutManifest: total, maximumFiles, maximumFileBytes, maximumTotalBytes });
}
