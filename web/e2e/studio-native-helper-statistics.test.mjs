import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compactDatabaseProcessEvidence } from './studio-native-database-scenario.mjs';
import { compactNativeProcessEvidence, encodeNativeEvidence, NativeEvidenceMaxBytes, persistNativeTerminalEvidence,
  projectNativeHelperStatistics } from './studio-native-evidence.mjs';

// Exercise the unchanged production function with in-memory child streams.
// Loading the whole runner would execute its actual native lifecycle.
const source = await readFile(new URL('./run-studio-native-real.mjs', import.meta.url), 'utf8');
const begin = source.indexOf('async function processAction(');
const end = source.indexOf('\nasync function snapshot(', begin);
assert.ok(begin >= 0 && end > begin, 'Production processAction source boundary must be present.');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const invoke = new AsyncFunction('spawn', 'check', 'counters', 'databaseRecoveryRequested', 'pwsh', 'helper', 'repository',
  'helpers', 'mainDeadline', 'cleanupDeadline', 'projectNativeHelperStatistics', 'process', 'setTimeout', 'clearTimeout',
  'action', 'payload', 'final', `${source.slice(begin, end)}\nreturn processAction(action, payload, final);`);
const bounded = { timeout: 5000 };
const unknown = { cimQueries: null, cachedPids: null, elapsedSeconds: null };
const observed = { cimQueries: 15, cachedPids: 16, elapsedSeconds: 20.19123456789 };
const identity = (processId, parentProcessId = 600) => ({ processId, parentProcessId,
  creationTimeUtc: '2026-10-07T20:30:00.0000000Z', commandLine: `pwsh.exe --owned-${processId}`,
  executablePath: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe', parentChain: [] });
const runnerIdentity = identity(600, 0);

function harness(plan) {
  const child = new EventEmitter();
  child.pid = 701;
  child.stdin = new EventEmitter();
  child.stdout = new EventEmitter();
  child.stderr = new EventEmitter();
  const inputs = [];
  const launches = [];
  const helpers = [];
  const timers = [];
  const cleared = [];
  const counters = { helpers: 0 };
  child.stdin.end = (value) => { inputs.push(value); queueMicrotask(() => plan(child)); };
  const spawn = (...args) => { launches.push(args); return child; };
  const pending = invoke(spawn, () => {}, counters, false, 'C:\\Program Files\\PowerShell\\7\\pwsh.exe',
    'D:\\source\\SonnetDB\\web\\e2e\\studio-native-process.ps1', 'D:\\source\\SonnetDB', helpers,
    Date.now() + 60_000, Date.now() + 60_000, projectNativeHelperStatistics, { pid: 600 },
    (callback, milliseconds) => { const timer = { callback, milliseconds }; timers.push(timer); return timer; },
    (timer) => cleared.push(timer), 'snapshot', { processIds: [600], descendants: false }, false);
  return { child, pending, helpers, timers, cleared, inputs, launches, counters };
}

function handshake(child, overrides = {}) {
  child.stdout.emit('data', Buffer.from(`${JSON.stringify({ kind: 'helper', identity: identity(child.pid), version: '7.6.6', ...overrides })}\n`));
}

function finish(child, envelopes, exitCode = 0) {
  const list = Array.isArray(envelopes) ? envelopes : [envelopes];
  assert.ok(list.length >= 1 && list.length <= 2);
  child.stdout.emit('data', Buffer.from(`${list.map((item) => JSON.stringify(item)).join('\n')}\n`));
  child.emit('exit', exitCode);
  child.emit('close', exitCode);
}

function compact(helpers) {
  return compactNativeProcessEvidence({ runnerIdentity, studioIdentity: null, events: [], helpers, streamCounts: {} });
}

function memoryWriter() {
  const files = new Map();
  return { files, write: async (name, value) => { files.set(name, encodeNativeEvidence(value)); } };
}

function terminalInput(writer, details, fatal) {
  return { write: writer.write, normalExit: { normalExit: false },
    cleanup: { cleanupProven: false, errors: [], fallbackActions: [], helperReclaims: [], allFourPortsReleased: false },
    result: { passed: false, fatal }, details, deadline: Date.now() + 4000 };
}

test('success retains only its envelope statistics and the complete original business result', bounded, async () => {
  const result = { identities: [identity(600, 0)], inspected: 1, additional: { original: true } };
  const run = harness((child) => {
    handshake(child);
    finish(child, [{ kind: 'result', result, ...observed }, { kind: 'error', message: 'Ignored error envelope.', cimQueries: 99, cachedPids: 99, elapsedSeconds: 99 }]);
  });
  assert.deepEqual(await run.pending, result);
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), observed);
  assert.deepEqual(projectNativeHelperStatistics(compact(run.helpers).helpers[0]), observed);
  assert.equal(run.helpers[0].exitCode, 0);
  assert.equal(run.helpers[0].timedOut, undefined);
  assert.equal(run.counters.helpers, 1);
  assert.equal(run.launches.length, 1);
  assert.equal(run.timers[0].milliseconds, 25_000);
  assert.deepEqual(run.cleared, run.timers);
  assert.deepEqual(JSON.parse(run.inputs[0]), { processIds: [600], descendants: false });
});

test('an error retains its own statistics without replacing the original primary message', bounded, async () => {
  const message = 'Native process helper budget exceeded (original primary).';
  const run = harness((child) => {
    handshake(child);
    finish(child, [{ kind: 'result', result: { ignored: true }, cimQueries: 99, cachedPids: 99, elapsedSeconds: 99 },
      { kind: 'error', message, ...observed }], 1);
  });
  await assert.rejects(run.pending, (error) => error.message === message);
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), observed);
  assert.deepEqual(projectNativeHelperStatistics(compact(run.helpers).helpers[0]), observed);
  assert.equal(run.helpers[0].exitCode, 1);
  assert.deepEqual(run.cleared, run.timers);
});

test('a missing result still rejects with the original error even when exit code is zero', bounded, async () => {
  const run = harness((child) => { handshake(child); finish(child, { kind: 'error', message: 'No business result.', ...observed }); });
  await assert.rejects(run.pending, (error) => error.message === 'No business result.');
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), observed);
});

test('missing and malformed observations stay unknown and never coerce or fabricate zero', bounded, () => {
  const cases = [undefined, null, {}, { cimQueries: '15', cachedPids: '16', elapsedSeconds: '20.19' },
    { cimQueries: -1, cachedPids: 161, elapsedSeconds: -1 }, { cimQueries: 1.5, cachedPids: Number.MAX_SAFE_INTEGER + 1, elapsedSeconds: Infinity },
    { cimQueries: NaN, cachedPids: null, elapsedSeconds: NaN }, Object.create(observed)];
  const expires = Date.now() + 1000;
  for (let index = 0; index < cases.length && index < 8; index += 1) {
    assert.ok(Date.now() < expires, 'Observation case budget exceeded.');
    assert.deepEqual(projectNativeHelperStatistics(cases[index]), unknown);
  }
  assert.deepEqual(projectNativeHelperStatistics({ cimQueries: 0, cachedPids: 160, elapsedSeconds: 123.456789 }),
    { cimQueries: 0, cachedPids: 160, elapsedSeconds: 123.456789 });
  assert.deepEqual(projectNativeHelperStatistics({ cimQueries: 1, cachedPids: 'bad', elapsedSeconds: 25.00001 }),
    { cimQueries: 1, cachedPids: null, elapsedSeconds: 25.00001 });
});

test('accessors and failing Proxy observations are not invoked or allowed to throw', bounded, () => {
  let getterCalls = 0;
  const accessors = Object.defineProperties({}, {
    cimQueries: { get() { getterCalls += 1; throw new Error('Raw credential must not escape.'); } },
    cachedPids: { get() { getterCalls += 1; return 16; } }, elapsedSeconds: { get() { getterCalls += 1; return 20; } },
  });
  assert.deepEqual(projectNativeHelperStatistics(accessors), unknown);
  assert.equal(getterCalls, 0);
  let descriptorCalls = 0;
  const proxy = new Proxy(observed, { getOwnPropertyDescriptor() { descriptorCalls += 1; throw new Error('Observation rejected.'); } });
  assert.deepEqual(projectNativeHelperStatistics(proxy), unknown);
  assert.equal(descriptorCalls, 3);
  const revoked = Proxy.revocable({}, {});
  revoked.revoke();
  assert.deepEqual(projectNativeHelperStatistics(revoked.proxy), unknown);
});

test('missing statistics do not alter a successful result or manufacture observations', bounded, async () => {
  const result = { identities: [], inspected: 0 };
  const run = harness((child) => { handshake(child); finish(child, { kind: 'result', result }); });
  assert.deepEqual(await run.pending, result);
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), unknown);
  assert.deepEqual(projectNativeHelperStatistics(compact(run.helpers).helpers[0]), unknown);
});

test('illegal error statistics remain unknown while the original error still rejects', bounded, async () => {
  const run = harness((child) => {
    handshake(child); finish(child, { kind: 'error', message: 'Original rejected identity.', cimQueries: 161, cachedPids: '16', elapsedSeconds: -1 }, 1);
  });
  await assert.rejects(run.pending, (error) => error.message === 'Original rejected identity.');
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), unknown);
});

test('stdout oversize retains the original cap failure without accepting late statistics', bounded, async () => {
  const run = harness((child) => {
    handshake(child);
    child.stdout.emit('data', Buffer.from('x'.repeat(NativeEvidenceMaxBytes + 1)));
    finish(child, { kind: 'error', message: 'Must not become the primary.', ...observed }, 1);
  });
  await assert.rejects(run.pending, (error) => error.message === 'PowerShell helper output cap exceeded.');
  assert.equal(Object.hasOwn(run.helpers[0], 'cimQueries'), false);
  assert.deepEqual(projectNativeHelperStatistics(compact(run.helpers).helpers[0]), unknown);
});

test('a wrong handshake identity rejects before statistics can enter the helper record', bounded, async () => {
  const run = harness((child) => {
    handshake(child, { identity: identity(999) });
    finish(child, { kind: 'result', result: { ignored: true }, ...observed });
  });
  await assert.rejects(run.pending, (error) => error.message === 'PowerShell 7 helper launch identity could not be proved.');
  assert.equal(Object.hasOwn(run.helpers[0], 'cimQueries'), false);
  assert.deepEqual(projectNativeHelperStatistics(run.helpers[0]), unknown);
});

test('a timeout and late output preserve the primary timeout and grant no diagnostic or action authority', bounded, async () => {
  const run = harness((child) => handshake(child));
  await Promise.resolve();
  assert.equal(run.timers.length, 1);
  run.timers[0].callback();
  await assert.rejects(run.pending, (error) => error.message === 'Owned PowerShell snapshot helper exceeded its 25000ms deadline.');
  assert.equal(run.helpers[0].timedOut, true);
  finish(run.child, { kind: 'result', result: { acted: true }, ...observed });
  assert.equal(Object.hasOwn(run.helpers[0], 'cimQueries'), false);
  assert.equal(Object.hasOwn(run.helpers[0], 'cachedPids'), false);
  assert.equal(Object.hasOwn(run.helpers[0], 'elapsedSeconds'), false);
  assert.equal(Object.hasOwn(run.helpers[0], 'acted'), false);
  assert.deepEqual(projectNativeHelperStatistics(compact(run.helpers).helpers[0]), unknown);
  assert.equal(compact(run.helpers).helpers[0].timedOut, true);
  assert.deepEqual(run.cleared, run.timers);
});

test('credential and arbitrary envelope properties cannot enter the fixed statistics projection', bounded, async () => {
  const secret = 'private-native-credential';
  const run = harness((child) => {
    handshake(child); finish(child, { kind: 'error', message: 'Primary helper rejection.', cimQueries: secret, cachedPids: 16,
      elapsedSeconds: 20.19, authorization: secret, arbitrary: { token: secret } }, 1);
  });
  await assert.rejects(run.pending, (error) => error.message === 'Primary helper rejection.');
  const detail = compact(run.helpers);
  const text = encodeNativeEvidence(detail, [secret]);
  assert.equal(text.includes(secret), false);
  assert.equal(Object.hasOwn(detail.helpers[0], 'authorization'), false);
  assert.equal(Object.hasOwn(detail.helpers[0], 'arbitrary'), false);
  assert.deepEqual(projectNativeHelperStatistics(detail.helpers[0]), { cimQueries: null, cachedPids: 16, elapsedSeconds: 20.19 });
});

test('compact observations refuse accessors while preserving full original identity and exit evidence', bounded, () => {
  let getterCalls = 0;
  const item = { processId: 701, parentProcessId: 600, identity: identity(701), action: 'snapshot', exitCode: 1,
    startedAtUtc: '2026-10-07T20:30:00Z', exitedAtUtc: '2026-10-07T20:30:21Z', command: ['pwsh.exe', '-Action', 'snapshot'], stderrBytes: 0 };
  Object.defineProperty(item, 'elapsedSeconds', { get() { getterCalls += 1; throw new Error('Must not be read.'); } });
  const detail = compact([item]);
  assert.equal(getterCalls, 0);
  assert.deepEqual(projectNativeHelperStatistics(detail.helpers[0]), unknown);
  assert.equal(detail.helpers[0].exitCode, 1);
  assert.deepEqual(detail.helpers[0].command, item.command);
  assert.equal(detail.identities.find((entry) => entry.processId === 701).commandLine, item.identity.commandLine);
  assert.equal(detail.helpers[0].identityKey, '701:2026-10-07T20:30:00.0000000Z');
});

test('the original 96-helper segmentation retains statistics, identities and unchanged overflow refusal', bounded, () => {
  const helpers = Array.from({ length: 96 }, (_, index) => ({ processId: 1000 + index, parentProcessId: 600,
    identity: identity(1000 + index), action: 'snapshot', exitCode: index === 95 ? 1 : 0,
    cimQueries: index, cachedPids: index + 1, elapsedSeconds: 20.19 + index / 100 }));
  const input = { runnerIdentity, studioIdentity: null, events: [], helpers, streamCounts: {} };
  const value = compactDatabaseProcessEvidence(compactNativeProcessEvidence, input);
  assert.equal(value.complete, true);
  assert.equal(value.helperCount, 96);
  assert.equal(value.segments.length, 2);
  assert.deepEqual(value.segments.map((segment) => segment.helperCount), [64, 32]);
  const retained = value.segments.flatMap((segment) => segment.evidence.helpers);
  const expires = Date.now() + 1000;
  for (let index = 0; index < retained.length && index < 96; index += 1) {
    assert.ok(Date.now() < expires, 'Segment comparison budget exceeded.');
    assert.deepEqual(projectNativeHelperStatistics(retained[index]), projectNativeHelperStatistics(helpers[index]));
    assert.equal(retained[index].identityKey, `${1000 + index}:2026-10-07T20:30:00.0000000Z`);
  }
  assert.throws(() => compactDatabaseProcessEvidence(compactNativeProcessEvidence, { ...input, helpers: [...helpers, helpers[0]] }), /count/u);
  assert.throws(() => compactNativeProcessEvidence({ ...input, helpers }), /process-count/u);
});

test('an oversize compact detail cannot mask the primary error or suppress independent terminal writes', bounded, async () => {
  const writer = memoryWriter();
  const fatal = { stage: 'helper', message: 'Original helper primary failure.' };
  const detail = { ...compact([]), oversized: 'x'.repeat(NativeEvidenceMaxBytes) };
  const output = await persistNativeTerminalEvidence(terminalInput(writer, [{ name: 'process-events.json', value: detail }], fatal));
  assert.equal(output.passed, false);
  assert.deepEqual(output.result.fatal, fatal);
  assert.equal(output.outcomes.find((item) => item.name === 'process-events.json').failure, 'oversize');
  assert.equal(writer.files.has('normal-exit.json'), true);
  assert.equal(writer.files.has('cleanup.json'), true);
  assert.deepEqual(JSON.parse(writer.files.get('result.json')).fatal, fatal);
});

test('credential detail rejection preserves the primary error and does not invoke its writer', bounded, async () => {
  const writer = memoryWriter();
  const fatal = { stage: 'helper', message: 'Original helper primary failure.' };
  const secret = 'private-native-credential';
  const input = terminalInput(writer, [{ name: 'process-events.json', value: { ...compact([]), credential: secret } }], fatal);
  const output = await persistNativeTerminalEvidence({ ...input, secrets: [secret] });
  assert.equal(output.passed, false);
  assert.deepEqual(output.result.fatal, fatal);
  assert.equal(output.outcomes.find((item) => item.name === 'process-events.json').failure, 'credential');
  assert.equal(writer.files.has('process-events.json'), false);
  assert.deepEqual(JSON.parse(writer.files.get('result.json')).fatal, fatal);
});
