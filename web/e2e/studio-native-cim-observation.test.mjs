import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import { compactNativeProcessEvidence, encodeNativeEvidence, NativeEvidenceMaxBytes,
  projectNativeCimObservation, projectNativeHelperStatistics } from './studio-native-evidence.mjs';

const bounded = { timeout: 5000 };
const operation = (ordinal = 1, overrides = {}) => ({ ordinal, phase: 'seed-lookup', startedMilliseconds: 10,
  elapsedMilliseconds: 21_537.2204, outcome: 'returned', resultCount: 1, ...overrides });
const observation = (operations = [operation()], state = 'complete') => ({ schemaVersion: 1, state, operations });
const envelope = (value = observation()) => ({ cimObservation: value });
const identity = (processId, parentProcessId = 600) => ({ processId, parentProcessId,
  creationTimeUtc: '2026-10-08T00:36:00.0000000Z', commandLine: `pwsh.exe --fixture-${processId}`,
  executablePath: 'C:\\Program Files\\PowerShell\\7\\pwsh.exe', parentChain: [] });
const source = await readFile(new URL('./run-studio-native-real.mjs', import.meta.url), 'utf8');
const begin = source.indexOf('async function processAction(');
const end = source.indexOf('\nasync function snapshot(', begin);
assert.ok(begin >= 0 && end > begin && end - begin < 10_000);
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const invoke = new AsyncFunction('spawn', 'check', 'counters', 'databaseRecoveryRequested', 'pwsh', 'helper', 'repository',
  'helpers', 'mainDeadline', 'cleanupDeadline', 'projectNativeHelperStatistics', 'projectNativeCimObservation', 'process',
  'setTimeout', 'clearTimeout', 'action', 'payload', `${source.slice(begin, end)}\nreturn processAction(action, payload, false);`);

function harness(plan, action = 'snapshot') {
  const child = new EventEmitter();
  Object.assign(child, { pid: 701, stdin: new EventEmitter(), stdout: new EventEmitter(), stderr: new EventEmitter() });
  const helpers = [];
  const counters = { helpers: 0 };
  const inputs = [];
  const timers = [];
  const cleared = [];
  const launches = [];
  child.stdin.end = (value) => { inputs.push(value); queueMicrotask(() => plan(child)); };
  const pending = invoke((...args) => { launches.push(args); return child; }, () => {}, counters, false,
    'C:\\Program Files\\PowerShell\\7\\pwsh.exe', 'fixture.ps1', 'D:\\source\\SonnetDB', helpers,
    Date.now() + 60_000, Date.now() + 60_000, projectNativeHelperStatistics, projectNativeCimObservation, { pid: 600 },
    (callback, milliseconds) => { const timer = { callback, milliseconds }; timers.push(timer); return timer; },
    (timer) => cleared.push(timer), action, { processIds: [600], descendants: false });
  return { child, pending, helpers, counters, inputs, timers, cleared, launches };
}

function handshake(child, processId = child.pid) {
  child.stdout.emit('data', Buffer.from(`${JSON.stringify({ kind: 'helper', identity: identity(processId), version: '7.6.6' })}\n`));
}

function finish(child, value, code = 0) {
  child.stdout.emit('data', Buffer.from(`${JSON.stringify(value)}\n`));
  child.emit('exit', code);
  child.emit('close', code);
}

function compact(helpers) {
  return compactNativeProcessEvidence({ runnerIdentity: identity(600, 0), studioIdentity: null, events: [], helpers, streamCounts: {} });
}

test('WB72 micro retains one observation without truncating a query over twenty seconds', bounded, () => {
  assert.deepEqual(projectNativeCimObservation(envelope()), observation());
});

test('fixed projection strips foreign fields and retains partial slots and all four phases', bounded, () => {
  const phases = ['self-handshake', 'parent-chain', 'seed-lookup', 'child-enumeration'];
  const expires = performance.now() + 1000;
  const operations = phases.map((phase, index) => {
    assert.ok(performance.now() < expires);
    return operation(index + 1, { phase, startedMilliseconds: index * 30_000, resultCount: phase === 'child-enumeration' ? 64 : 1,
      commandLine: 'secret-command', processId: 99, message: 'secret-error' });
  });
  operations[3].elapsedMilliseconds = null;
  operations[3].outcome = 'unknown';
  operations[3].resultCount = null;
  const projected = projectNativeCimObservation(envelope(observation(operations, 'unknown')));
  assert.equal(projected.state, 'unknown');
  assert.equal(projected.operations.length, 4);
  assert.equal(projected.operations[3].elapsedMilliseconds, null);
  assert.equal(encodeNativeEvidence(projected).includes('secret'), false);
  assert.deepEqual(Object.keys(projected.operations[0]), ['ordinal', 'phase', 'startedMilliseconds', 'elapsedMilliseconds', 'outcome', 'resultCount']);
});

test('slot and cardinality limits accept 160 actual calls and reject malformed or oversized observations', bounded, () => {
  const expires = performance.now() + 1000;
  const operations = Array.from({ length: 160 }, (_, index) => {
    assert.ok(performance.now() < expires);
    return operation(index + 1, { startedMilliseconds: index * 30_000 });
  });
  assert.equal(projectNativeCimObservation(envelope(observation(operations))).operations.length, 160);
  assert.equal(projectNativeCimObservation(envelope(observation([...operations, operation(161)]))), null);
  const cases = [operation(2), operation(1, { phase: 'unknown' }), operation(1, { resultCount: 2 }),
    operation(1, { phase: 'child-enumeration', resultCount: 65 }), operation(1, { startedMilliseconds: -1 }),
    operation(1, { elapsedMilliseconds: Infinity }), operation(1, { outcome: 'threw', resultCount: 1 }),
    operation(1, { elapsedMilliseconds: '21' }), operation(1, { startedMilliseconds: null })];
  for (let index = 0; index < cases.length && index < 9; index += 1) {
    assert.ok(performance.now() < expires);
    assert.equal(projectNativeCimObservation(envelope(observation([cases[index]]))), null);
  }
  assert.equal(projectNativeCimObservation(envelope(observation([operation(), operation(2, { startedMilliseconds: 1 })]))), null);
});

test('missing, inherited, accessor and throwing Proxy data stay unknown without invoking getters', bounded, () => {
  let getterCalls = 0;
  const accessor = Object.defineProperty({}, 'cimObservation', { get() { getterCalls += 1; throw new Error('secret'); } });
  const nested = Object.defineProperty(operation(), 'elapsedMilliseconds', { get() { getterCalls += 1; return 1; } });
  assert.equal(projectNativeCimObservation(accessor), null);
  assert.equal(projectNativeCimObservation(envelope(observation([nested]))), null);
  assert.equal(projectNativeCimObservation(Object.create(envelope())), null);
  assert.equal(projectNativeCimObservation({}), null);
  assert.equal(getterCalls, 0);
  const proxy = new Proxy(envelope(), { getOwnPropertyDescriptor() { throw new Error('secret'); } });
  assert.equal(projectNativeCimObservation(proxy), null);
  const revoked = Proxy.revocable({}, {});
  revoked.revoke();
  assert.equal(projectNativeCimObservation(revoked.proxy), null);
});

test('the independent projection deadline or clock failure returns unknown and non-snapshot actions do not inspect data', bounded, () => {
  let ticks = 0;
  assert.equal(projectNativeCimObservation(envelope(), { now: () => { ticks += 25; return ticks; } }), null);
  assert.equal(projectNativeCimObservation(envelope(), { now: () => { throw new Error('clock'); } }), null);
  assert.equal(projectNativeCimObservation(envelope(), { now: () => NaN }), null);
  assert.equal(projectNativeCimObservation(envelope(), { now: () => Infinity }), null);
  assert.equal(projectNativeCimObservation(envelope(), { deadline: NaN }), null);
  let backwards = 100;
  assert.equal(projectNativeCimObservation(envelope(), { now: () => backwards-- }), null);
  let calls = 0;
  const hostile = new Proxy({}, { getOwnPropertyDescriptor() { calls += 1; throw new Error('must not inspect'); } });
  assert.equal(projectNativeCimObservation(hostile, { action: 'close' }), null);
  assert.equal(projectNativeCimObservation(hostile, { action: 'kill' }), null);
  assert.equal(calls, 0);
});

test('success preserves the complete original result and carries only its same-envelope observation to terminal compaction', bounded, async () => {
  const result = { identities: [], inspected: 1, untouched: { original: true } };
  const run = harness((child) => { handshake(child); finish(child, { kind: 'result', result, ...envelope(), cimQueries: 1, cachedPids: 1, elapsedSeconds: 21.54 }); });
  assert.deepEqual(await run.pending, result);
  assert.deepEqual(run.helpers[0].cimObservation, observation());
  assert.deepEqual(compact(run.helpers).helpers[0].cimObservation, observation());
  assert.equal(run.launches.length, 1);
  assert.equal(run.counters.helpers, 1);
  assert.equal(run.timers[0].milliseconds, 25_000);
  assert.deepEqual(JSON.parse(run.inputs[0]), { processIds: [600], descendants: false });
  assert.deepEqual(run.cleared, run.timers);
});

test('query failure preserves the primary message and returned-prefix observation without granting authority', bounded, async () => {
  const value = observation([operation(), operation(2, { startedMilliseconds: 30_000, outcome: 'threw', resultCount: null })]);
  const run = harness((child) => { handshake(child); finish(child, { kind: 'error', message: 'Original CIM query failed.', ...envelope(value), acted: true }, 1); });
  await assert.rejects(run.pending, (error) => error.message === 'Original CIM query failed.');
  assert.deepEqual(compact(run.helpers).helpers[0].cimObservation, value);
  assert.equal(Object.hasOwn(run.helpers[0], 'acted'), false);
});

test('malformed diagnostic data cannot change success or the original failure and mutations retain null', bounded, async () => {
  const success = harness((child) => { handshake(child); finish(child, { kind: 'result', result: { original: true }, ...envelope({ arbitrary: 'secret' }) }); });
  assert.deepEqual(await success.pending, { original: true });
  assert.equal(success.helpers[0].cimObservation, null);
  const failure = harness((child) => { handshake(child); finish(child, { kind: 'error', message: 'Original rejection.', ...envelope({ arbitrary: 'secret' }) }, 1); });
  await assert.rejects(failure.pending, /Original rejection/u);
  assert.equal(failure.helpers[0].cimObservation, null);
  const mutation = harness((child) => { handshake(child); finish(child, { kind: 'result', result: { acted: false }, ...envelope() }); }, 'close');
  assert.deepEqual(await mutation.pending, { acted: false });
  assert.equal(mutation.helpers[0].cimObservation, null);
});

test('timeout and late output cannot enter the current helper observation record', bounded, async () => {
  const run = harness((child) => handshake(child));
  await Promise.resolve();
  run.timers[0].callback();
  await assert.rejects(run.pending, /exceeded its 25000ms deadline/u);
  finish(run.child, { kind: 'result', result: { acted: true }, ...envelope() });
  assert.equal(Object.hasOwn(run.helpers[0], 'cimObservation'), false);
  assert.equal(compact(run.helpers).helpers[0].cimObservation, null);
});

test('invalid handshake and output cap reject before observation acceptance', bounded, async () => {
  const identityFailure = harness((child) => { handshake(child, 999); finish(child, { kind: 'result', result: {}, ...envelope() }); });
  await assert.rejects(identityFailure.pending, /launch identity could not be proved/u);
  assert.equal(Object.hasOwn(identityFailure.helpers[0], 'cimObservation'), false);
  const oversize = harness((child) => {
    handshake(child);
    child.stdout.emit('data', Buffer.from('x'.repeat(NativeEvidenceMaxBytes + 1)));
    finish(child, { kind: 'result', result: {}, ...envelope() });
  });
  await assert.rejects(oversize.pending, /output cap exceeded/u);
  assert.equal(Object.hasOwn(oversize.helpers[0], 'cimObservation'), false);
});
