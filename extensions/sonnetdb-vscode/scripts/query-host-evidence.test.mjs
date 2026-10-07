import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import test from 'node:test';
import { acceptOwnedIdentity, attemptIndependentSteps, captureOwnedSnapshot, ownedIdentityFailure, recordOwnedIdentityEvent,
  validateOwnedIdentity, validateOwnedIdentityAnchor, validateOwnedIdentityLedger } from './query-host-evidence.mjs';

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
  assert.equal(ledger.get(11).parentChain.length, 1);
  assert.equal(ledger.get(11).parentChain[0].commandLine, 'node runner');
  assert.equal(ledger.get(11).externalAncestorCount, 1);
  assert.equal(ledger.get(11).externalAncestorsOmitted, true);
  assert.equal(ledger.get(11).externalAncestorsSha256,
    createHash('sha256').update(JSON.stringify(external.parentChain.slice(1))).digest('hex').toUpperCase());
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

test('normalization retains every full tuple before and including the exact anchor', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const value = { ...child(11, 'x'.repeat(8000)), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: 'y'.repeat(9000) },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
    { pid: 1, parentPid: 90, created, commandLine: 'external diagnostic'.repeat(1000) },
  ] };
  const accepted = acceptOwnedIdentity(value, identities, { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' });
  assert.equal(accepted.commandLine, value.commandLine);
  assert.deepEqual(accepted.parentChain, value.parentChain.slice(0, 2));
  assert.equal(accepted.externalAncestorCount, 1);
  assert.equal(JSON.stringify(accepted).includes('external diagnostic'), false);
  assert.doesNotThrow(() => validateOwnedIdentityAnchor(accepted, owner));
  const acceptedOwner = acceptOwnedIdentity(owner, identities, { ...options, eventName: 'runner' });
  assert.deepEqual(acceptedOwner.parentChain, []);
  assert.doesNotThrow(() => validateOwnedIdentity(acceptedOwner, 10));
});

test('unsafe original external text is rejected before omission with a fixed safe reason', { timeout: 1000 }, () => {
  const marker = 'SENTINEL credential must never enter evidence';
  const identities = new Map([[10, owner]]);
  const value = { ...child(11), parentChain: [...child(11).parentChain.slice(0, 1),
    { pid: 1, parentPid: 0, created, commandLine: marker }] };
  assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper',
    validateText: (text) => { if (text.includes(marker)) throw new Error(marker); } }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 11), { stage: 'unsafeText', pid: 11, reason: 'unsafe_identity_text_preserved' });
    assert.equal(error.message.includes(marker), false); return true;
  });
  assert.equal(identities.has(11), false);
});

test('the 256 KiB ledger rejects a complete oversized addition without losing accepted identities', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const first = acceptOwnedIdentity(child(11, 'a'.repeat(131072)), identities,
    { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' });
  assert.throws(() => acceptOwnedIdentity(child(12, 'b'.repeat(131072)), identities,
    { ...options, validateLedger: validateOwnedIdentityLedger, eventName: 'helper' }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 12), { stage: 'ledger', pid: 12, reason: 'ledger_budget_exceeded' }); return true;
  });
  assert.equal(identities.get(11), first);
  assert.equal(first.commandLine.length, 131072);
  assert.equal(identities.has(12), false);
  assert.ok(Buffer.byteLength(JSON.stringify([...identities.values()], null, 2)) <= 256 * 1024);
});

test('the unchanged 128 identity cap refuses a new complete identity with its own reason', { timeout: 1000 }, () => {
  const identities = new Map(Array.from({ length: 128 }, (_value, index) => [index + 20, child(index + 20)]));
  assert.throws(() => acceptOwnedIdentity(child(200), identities, { ...options, eventName: 'helper' }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 200), { stage: 'identity', pid: 200, reason: 'identity_count_exceeded' }); return true;
  });
  assert.equal(identities.size, 128);
});

test('secondary events bind the exact full command and creation to the authoritative ledger', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [];
  const accepted = acceptOwnedIdentity(child(11, 'full command '.repeat(4000)), identities, { ...options, eventName: 'helper',
    recordEvent: (value) => recordOwnedIdentityEvent({ ...value, command: ['unretained extra command'] }, identities, events, options) });
  const event = events[0];
  assert.deepEqual(event.identityLedgerRef, { pid: accepted.pid, created: accepted.created });
  assert.equal(event.parentPid, accepted.parentPid);
  assert.equal(event.commandLineSha256, createHash('sha256').update(accepted.commandLine).digest('hex').toUpperCase());
  assert.equal(event.parentChainLedgerCreated, accepted.created);
  assert.equal(Object.hasOwn(event, 'commandLine'), false);
  assert.equal(Object.hasOwn(event, 'command'), false);
  assert.equal(Object.hasOwn(event, 'parentChain'), false);
  assert.equal(accepted.commandLine.length, 52000);
  assert.ok(Buffer.byteLength(JSON.stringify(event)) < 1024);
  assert.throws(() => recordOwnedIdentityEvent({ ...accepted, created: '2026-10-07T00:01:00.000Z' }, identities, events, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_identity_reference_mismatch'); return true;
  });
  assert.throws(() => recordOwnedIdentityEvent({ ...accepted, parentChain: [{ ...accepted.parentChain[0], commandLine: 'changed parent' }] }, identities, events, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_identity_reference_mismatch'); return true;
  });
  assert.equal(events.length, 1);
});

test('secondary event byte and count caps remain failures while the accepted full ledger survives', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [{ padding: 'x'.repeat(192 * 1024) }];
  const failures = captureOwnedSnapshot([], identities, [], { ...options,
    discover: (_snapshot, values) => values.set(11, child(11)),
    recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
  assert.deepEqual(failures, [{ stage: 'event', pid: 11, reason: 'event_budget_exceeded' }]);
  assert.equal(identities.get(11).commandLine, 'owned child');
  assert.equal(events.length, 1);
  const countEvents = Array.from({ length: 256 }, () => ({}));
  assert.throws(() => recordOwnedIdentityEvent(identities.get(11), identities, countEvents, options), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'event_count_exceeded'); return true;
  });
  assert.equal(countEvents.length, 256);
});

test('a missing or changed anchor cannot bind ownership and changed pre-anchor tuples cannot replace it', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]);
  const value = { ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 10, created, commandLine: 'full intermediate parent' },
    { pid: 10, parentPid: 1, created, commandLine: 'node runner' },
  ] };
  const accepted = acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' });
  assert.throws(() => validateOwnedIdentityAnchor(accepted, { ...owner, commandLine: 'changed owner' }));
  assert.throws(() => validateOwnedIdentityAnchor({ ...value, parentChain: value.parentChain.slice(0, 1) }, owner));
  assert.throws(() => acceptOwnedIdentity({ ...value, parentChain: [{ ...value.parentChain[0], commandLine: 'changed parent' }, value.parentChain[1]] },
    identities, { ...options, eventName: 'helper' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).reason, 'recorded_identity_changed_replacement_preserved'); return true;
  });
  const differentAnchor = { ...child(13), parentChain: [{ ...child(13).parentChain[0], created: '2026-10-06T00:00:00.000Z' }] };
  assert.throws(() => acceptOwnedIdentity(differentAnchor, identities, { ...options, eventName: 'helper' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 13).reason, 'ownership_anchor_changed_preserved'); return true;
  });
  assert.equal(identities.get(11), accepted);
  assert.equal(identities.has(13), false);
});

test('unsafe secondary event text is not retained and still leaves complete cleanup authority', { timeout: 1000 }, () => {
  const identities = new Map([[10, owner]]); const events = [];
  assert.throws(() => acceptOwnedIdentity(child(11), identities, { ...options, eventName: 'helper',
    recordEvent: (value) => recordOwnedIdentityEvent({ ...value, unsafe: 'event marker' }, identities, events,
      { ...options, validateText: (text) => { assert.equal(text.includes('event marker'), false); } }) }), (error) => {
    assert.deepEqual(ownedIdentityFailure(error, 11), { stage: 'event', pid: 11, reason: 'unsafe_event_text_preserved' }); return true;
  });
  assert.equal(events.length, 0);
  assert.equal(identities.get(11).commandLine, 'owned child');
});
