import assert from 'node:assert/strict';
import test from 'node:test';
import { acceptOwnedIdentity, attemptIndependentSteps, captureOwnedSnapshot, validateOwnedIdentity } from './query-host-evidence.mjs';

const created = '2026-10-07T00:00:00.000Z';
const owner = { pid: 10, parentPid: 1, created, commandLine: 'node runner', parentChain: [{ pid: 1, unavailable: true }] };
const child = (pid, commandLine = 'owned child') => ({ pid, parentPid: 10, created, commandLine,
  parentChain: [{ pid: 10, parentPid: 1, created, commandLine: 'node runner' }, { pid: 1, unavailable: true }] });
const options = { ownerPid: 10, validateText: () => {}, recordEvent: () => {}, clock: () => 1000 };

test('partial discovery retains accepted identities and a traversal failure', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const events = [];
  const failures = captureOwnedSnapshot([], identities, [], { ...options, recordEvent: (value) => events.push(value),
    discover: (_snapshot, values) => { values.set(11, child(11)); throw new Error('injected traversal failure'); } });
  assert.equal(identities.get(11).commandLine, 'owned child');
  assert.equal(events[0].pid, 11);
  assert.deepEqual(failures, [{ stage: 'discovery', reason: 'partial_discovery_failed' }]);
});

test('expired discovery still flushes its safe partial ledger under a fresh bounded clock', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); let now = 1000;
  const failures = captureOwnedSnapshot([], identities, [], { ...options, clock: () => now,
    discover: (_snapshot, values) => { values.set(11, child(11)); now += 3000; throw new Error('injected discovery deadline'); } });
  assert.equal(identities.get(11).pid, 11);
  assert.equal(failures[0].reason, 'partial_discovery_failed');
});

test('secondary event failure preserves the full safety ledger for independent cleanup', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]);
  const failures = captureOwnedSnapshot([], identities, [], { ...options, recordEvent: () => { throw new Error('injected log failure'); },
    discover: (_snapshot, values) => values.set(11, child(11)) });
  assert.equal(identities.get(11).parentChain[0].commandLine, 'node runner');
  assert.deepEqual(failures, [{ stage: 'event', pid: 11, reason: 'secondary_event_failed' }]);
  const stopped = [];
  const steps = await attemptIndependentSteps([{ name: 'audit', run: async () => { throw new Error('injected audit failure'); } },
    { name: 'safe-stop', run: async () => { validateOwnedIdentity(identities.get(11), 10); stopped.push(11); } }], { deadline: 2000, clock: () => 1000 });
  assert.deepEqual(stopped, [11]);
  assert.equal(steps[0].ok, false);
  assert.equal(steps[1].ok, true);
  // A successful cleanup never converts an earlier audit failure into PASS.
  assert.equal(failures.length === 0 && steps.every((step) => step.ok), false);
});

test('incomplete descendants remain unaccepted and cannot authorize cleanup', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const failures = captureOwnedSnapshot([], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, child(11, null)) });
  assert.equal(identities.has(11), false);
  assert.deepEqual(failures, [{ stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' }]);
});

test('a missing command before the ownership anchor cannot authorize cleanup', { timeout: 1000 }, () => {
  assert.throws(() => validateOwnedIdentity({ ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: null },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
  ] }, 10));
});

test('external ancestors after the full ownership anchor remain diagnostic only', { timeout: 1000 }, () => {
  const external = { ...child(11), parentChain: [
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
    { pid: 1, parentPid: 90, created, commandLine: null },
  ] };
  assert.doesNotThrow(() => validateOwnedIdentity(external, 10));
  const ledger = new Map();
  acceptOwnedIdentity(external, ledger, { ...options, eventName: 'helper' });
  assert.equal(ledger.get(11).ownershipAnchorPid, 10);
  assert.equal(ledger.get(11).externalAncestorsDiagnosticOnly, true);
  assert.equal(ledger.get(11).parentChain[1].commandLine, null);
  assert.doesNotThrow(() => validateOwnedIdentity({ ...owner, parentChain: [
    { pid: 1, parentPid: 90, created, commandLine: null },
  ] }, 10));
});

test('PID reuse and unanchored parent chains cannot replace accepted ownership', { timeout: 1000 }, () => {
  const identities = new Map([[11, child(11)]]);
  assert.throws(() => acceptOwnedIdentity({ ...child(11), created: '2026-10-07T00:01:00.000Z' }, identities, { ...options, eventName: 'helper' }));
  assert.equal(identities.get(11).created, created);
  assert.throws(() => validateOwnedIdentity({ ...child(12), parentPid: 90, parentChain: [{ pid: 90, unavailable: true }] }, 10));
});

test('terminal writers remain independent after detail and manifest failures', { timeout: 1000 }, async () => {
  const attempted = [];
  const names = ['process-events', 'child-output', 'cleanup', 'result', 'manifest'];
  const results = await attemptIndependentSteps(names.map((name) => ({ name, run: async () => {
    attempted.push(name); if (name === 'process-events' || name === 'manifest') throw new Error('injected terminal failure');
  } })), { deadline: 2000, clock: () => 1000 });
  assert.deepEqual(attempted, names);
  assert.equal(results.find((item) => item.name === 'cleanup').ok, true);
  assert.equal(results.find((item) => item.name === 'result').ok, true);
  assert.equal(results.every((item) => item.ok), false);
});

test('bounded steps honour cancellation represented by the closed deadline', { timeout: 1000 }, async () => {
  const results = await attemptIndependentSteps([{ name: 'cancelled', run: async () => { assert.fail('must not execute'); } }],
    { deadline: 1000, clock: () => 1000 });
  assert.deepEqual(results, [{ name: 'cancelled', attempted: false, ok: false, reason: 'step_deadline' }]);
});
