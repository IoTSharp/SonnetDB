import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, readFile, realpath, rmdir, unlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { acceptOwnedIdentity, attemptIndependentSteps, captureOwnedCandidateSnapshot, captureOwnedSnapshot, ownedIdentityFailure, recordOwnedIdentityEvent,
  validateOwnedIdentity, validateOwnedIdentityAnchor, validateOwnedIdentityLedger } from './query-host-evidence.mjs';

const created = '2026-10-07T00:00:00.000Z';
const owner = { pid: 10, parentPid: 1, created, commandLine: 'node runner', parentChain: [{ pid: 1, unavailable: true }] };
const child = (pid, commandLine = 'owned child') => ({ pid, parentPid: 10, created, commandLine,
  parentChain: [{ pid: 10, parentPid: 1, created, commandLine: 'node runner' }, { pid: 1, unavailable: true }] });
const options = { ownerPid: 10, validateText: () => {}, recordEvent: () => {}, clock: () => 1000 };

const intermediate = { pid: 12, parentPid: 10, created, commandLine: 'complete current intermediate' };
const pendingCandidate = { ...child(11, null), parentPid: 12, parentChain: [intermediate, owner] };
const initialCandidates = () => [owner, intermediate, { ...pendingCandidate }];
const candidateOptions = { ...options, discover: (snapshot, values) => values.set(11, snapshot.find((item) => item.pid === 11)) };

test('one exact fresh batch restores missing and empty own commands with a ledger-bound fixed observation', { timeout: 1000 }, async () => {
  const first = child(11, null); const second = child(13, ''); const initial = [owner, first, second];
  const identities = new Map([[10, owner]]); const events = []; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot(initial, identities, [], { ...options,
    discover: (_snapshot, values) => { values.set(11, first); values.set(13, second); },
    refreshSnapshot: async () => { refreshes += 1; return [owner, child(11, 'fresh complete command'), child(13, 'fresh second command')]; },
    recordEvent: (value) => recordOwnedIdentityEvent(value, identities, events, options) });
  assert.deepEqual(failures, []); assert.equal(refreshes, 1);
  assert.equal(identities.get(11).commandLine, 'fresh complete command');
  assert.deepEqual(identities.get(11).parentChain, [{ pid: 10, parentPid: 1, created, commandLine: 'node runner' }]);
  assert.deepEqual(events.map((value) => [value.candidateCommandRechecked, value.initialCommandState]), [[true, 'missing'], [true, 'empty']]);
  assert.deepEqual(events[0].identityLedgerRef, { pid: 11, created });
  assert.equal(Object.hasOwn(events[0], 'commandLine'), false);
});

test('fresh absence, PID reuse, parent changes, duplicates and remaining missing commands never become authority', { timeout: 1000 }, async () => {
  const freshChild = { ...pendingCandidate, commandLine: 'fresh complete command' };
  const cases = [
    [[owner, intermediate], 'candidate_snapshot_missing'],
    [[owner, intermediate, { ...freshChild, created: '2026-10-07T00:01:00.000Z' }], 'candidate_creation_changed'],
    [[owner, intermediate, { ...freshChild, parentPid: 10 }], 'candidate_parent_changed'],
    [[owner, intermediate, freshChild, { ...freshChild }], 'candidate_snapshot_duplicate'],
    [[owner, intermediate, { ...freshChild, commandLine: null }], 'identity_command_missing'],
    [[owner, intermediate, { ...freshChild, commandLine: '' }], 'identity_command_empty'],
    [[owner, intermediate, { ...freshChild, commandLine: 123 }], 'identity_command_invalid'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 7; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
      refreshSnapshot: async () => { refreshes += 1; return cases[index][0]; } });
    assert.equal(failures.length, 1); assert.equal(failures[0].subreason, cases[index][1]);
    assert.equal(refreshes, 1); assert.equal(identities.has(11), false);
    assert.equal(JSON.stringify(failures).includes('fresh complete command'), false);
  }
});

test('fresh authority requires every original complete parent and the exact live ledger anchor', { timeout: 1000 }, async () => {
  const freshChild = { ...pendingCandidate, commandLine: 'fresh complete command' };
  const cases = [
    [[owner, freshChild], 'candidate_snapshot_missing'],
    [[owner, { ...intermediate, commandLine: 'changed parent' }, freshChild], 'candidate_parent_tuple_changed'],
    [[owner, { ...intermediate, created: '2026-10-06T00:00:00.000Z' }, freshChild], 'candidate_parent_tuple_changed'],
    [[owner, { ...intermediate, commandLine: null }, freshChild], 'parent_command_missing'],
    [[intermediate, freshChild], 'candidate_snapshot_missing'],
    [[{ ...owner, commandLine: 'changed anchor' }, intermediate, freshChild], 'ownership_anchor_tuple_mismatch'],
    [[owner, { ...owner }, intermediate, freshChild], 'candidate_snapshot_duplicate'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 7; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]);
    const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
      refreshSnapshot: async () => cases[index][0] });
    assert.equal(failures[0].subreason, cases[index][1]); assert.equal(identities.has(11), false);
  }
});

test('invalid initial tuples and incomplete initial authority cannot trigger a command refresh', { timeout: 1000 }, async () => {
  const cases = [
    { ...pendingCandidate, pid: 0 }, { ...pendingCandidate, parentPid: -1 },
    { ...pendingCandidate, created: 'invalid original creation' }, { ...pendingCandidate, commandLine: 123 },
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 4; index += 1) {
    assert.ok(Date.now() < expires); const value = cases[index]; const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot([owner, intermediate, value], identities, [], { ...options,
      discover: (_snapshot, values) => values.set(value.pid, value), refreshSnapshot: async () => { refreshes += 1; return []; } });
    assert.ok(failures.length > 0); assert.equal(refreshes, 0); assert.equal(identities.size, 1);
  }
  const identities = new Map([[10, owner]]); let refreshes = 0;
  const duplicate = await captureOwnedCandidateSnapshot([...initialCandidates(), { ...pendingCandidate }], identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
  assert.equal(duplicate[0].subreason, 'candidate_snapshot_duplicate'); assert.equal(refreshes, 0); assert.equal(identities.size, 1);
  const failures = await captureOwnedCandidateSnapshot([owner, { ...intermediate, commandLine: null }, pendingCandidate], identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
  assert.equal(failures[0].subreason, 'parent_command_missing'); assert.equal(refreshes, 0);
});

test('current authority normalization permits diagnostic depth while retaining the original true twelve-hop limit', { timeout: 1000 }, async () => {
  const external = Array.from({ length: 12 }, (_value, index) => ({ pid: index + 101, parentPid: index === 11 ? 0 : index + 102,
    created, commandLine: `external ${index}` }));
  const own = { ...owner, parentPid: 101 }; const value = { ...child(11), parentChain: [own, ...external] };
  const identities = new Map([[10, own]]);
  const failures = await captureOwnedCandidateSnapshot([own, value, ...external], identities, [], { ...candidateOptions });
  assert.deepEqual(failures, []); assert.equal(identities.get(11).parentChain.length, 1);
  assert.equal(identities.get(11).externalAncestorCount, 12);
  const limitParents = Array.from({ length: 11 }, (_value, index) => ({ pid: index + 20, parentPid: index === 10 ? 10 : index + 21,
    created, commandLine: 'complete exact-limit parent' }));
  const exactLimit = { ...child(11), parentPid: 20, parentChain: [...limitParents, owner] };
  const limitLedger = new Map([[10, owner]]);
  const limitFailures = await captureOwnedCandidateSnapshot([owner, exactLimit, ...limitParents], limitLedger, [], { ...candidateOptions });
  assert.deepEqual(limitFailures, []); assert.equal(limitLedger.get(11).parentChain.length, 12);
  assert.deepEqual(limitLedger.get(11).parentChain,
    exactLimit.parentChain.map(({ pid, parentPid, created: time, commandLine }) => ({ pid, parentPid, created: time, commandLine })));
  const parents = Array.from({ length: 12 }, (_value, index) => ({ pid: index + 20, parentPid: index === 11 ? 10 : index + 21,
    created, commandLine: 'complete deep parent' }));
  const tooDeep = { ...child(11), parentPid: 20, parentChain: [...parents, owner] }; const deepLedger = new Map([[10, owner]]);
  const rejected = await captureOwnedCandidateSnapshot([owner, tooDeep, ...parents], deepLedger, [], { ...candidateOptions });
  assert.equal(rejected[0].subreason, 'parent_chain_length_exceeded'); assert.equal(deepLedger.has(11), false);
  assert.throws(() => validateOwnedIdentity(tooDeep, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_length_exceeded'); assert.equal(failure.failedField, 'parentChain');
    assert.equal(failure.completeness, 'over_limit'); return true;
  });
});

test('current snapshot cycles and discovered discontinuity stay fixed failures without retry', { timeout: 1000 }, async () => {
  const cases = [
    [[owner, { ...intermediate, parentPid: 11 }, pendingCandidate], 'parent_chain_cycle'],
    [[owner, intermediate, { ...pendingCandidate, parentChain: [owner] }], 'candidate_parent_tuple_changed'],
  ];
  const expires = Date.now() + 800;
  for (let index = 0; index < cases.length && index < 2; index += 1) {
    assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); let refreshes = 0;
    const failures = await captureOwnedCandidateSnapshot(cases[index][0], identities, [], { ...candidateOptions,
      refreshSnapshot: async () => { refreshes += 1; return initialCandidates(); } });
    assert.equal(failures[0].subreason, cases[index][1]); assert.equal(refreshes, 0); assert.equal(identities.has(11), false);
  }
});

test('raw initial and fresh external credential text is rejected before omission or hashing', { timeout: 1000 }, async () => {
  const marker = 'WB46_RAW_EXTERNAL_SYNTHETIC_SECRET';
  const validateText = (text) => { if (text.includes(marker)) throw new Error(marker); };
  const rawInitial = { ...pendingCandidate, parentChain: [intermediate, owner, { pid: 1, commandLine: marker }] };
  const identities = new Map([[10, owner]]); let refreshes = 0;
  const first = await captureOwnedCandidateSnapshot([owner, intermediate, rawInitial], identities, [], { ...candidateOptions, validateText,
    refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(first[0].reason, 'unsafe_identity_text_preserved'); assert.equal(refreshes, 0); assert.equal(identities.has(11), false);
  const second = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, validateText,
    refreshSnapshot: async () => [owner, intermediate, { ...pendingCandidate, commandLine: 'fresh owned command' },
      { pid: 1, parentPid: 0, created, commandLine: marker }] });
  assert.equal(second[0].reason, 'unsafe_identity_text_preserved'); assert.equal(identities.has(11), false);
  assert.equal(JSON.stringify([first, second, [...identities.values()]]).includes(marker), false);
});

test('partial discovery and secondary event failure retain complete fresh primary authority independently', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); let now = 1000;
  const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, clock: () => now,
    discover: (snapshot, values) => { values.set(11, snapshot.find((value) => value.pid === 11)); now += 3000; throw new Error('partial traversal'); },
    refreshSnapshot: async () => [owner, intermediate, { ...pendingCandidate, commandLine: 'complete fresh primary authority' }],
    recordEvent: () => { throw new Error('secondary sink failure'); } });
  assert.deepEqual(failures.map((value) => value.reason), ['partial_discovery_failed', 'secondary_event_failed']);
  assert.equal(identities.get(11).commandLine, 'complete fresh primary authority');
  assert.deepEqual(identities.get(11).parentChain.map((value) => value.pid), [12, 10]);
});

test('snapshot count and preparation deadlines stay bounded and never trigger blind refresh', { timeout: 1000 }, async () => {
  let refreshes = 0; const identities = new Map([[10, owner]]);
  const oversized = await captureOwnedCandidateSnapshot(Array.from({ length: 4097 }, () => owner), identities, [], {
    ...candidateOptions, refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(oversized[0].subreason, 'candidate_snapshot_count_exceeded'); assert.equal(refreshes, 0);
  const freshOversized = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
    refreshSnapshot: async () => { refreshes += 1; return Array.from({ length: 4097 }, () => owner); } });
  assert.equal(freshOversized[0].subreason, 'candidate_snapshot_count_exceeded'); assert.equal(refreshes, 1); assert.equal(identities.has(11), false);
  let ticks = 0;
  const expired = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions,
    clock: () => ticks++ === 0 ? 1000 : 4000, refreshSnapshot: async () => { refreshes += 1; return []; } });
  assert.equal(expired[0].subreason, 'candidate_snapshot_deadline'); assert.equal(identities.has(11), false); assert.equal(refreshes, 1);
});

test('a hung or late fresh callback is cancelled once and cannot admit after a terminal timeout', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); let release; let signal; let calls = 0;
  const failures = await captureOwnedCandidateSnapshot(initialCandidates(), identities, [], { ...candidateOptions, refreshMilliseconds: 20,
    refreshSnapshot: (request) => { calls += 1; signal = request.signal; return new Promise((resolve) => { release = resolve; }); } });
  assert.equal(calls, 1); assert.equal(signal.aborted, true); assert.equal(failures[0].subreason, 'candidate_refresh_failed');
  assert.equal(identities.has(11), false);
  release([owner, intermediate, { ...pendingCandidate, commandLine: 'late command must not be admitted' }]);
  await Promise.resolve(); await Promise.resolve();
  assert.equal(identities.has(11), false); assert.equal(failures[0].subreason, 'candidate_refresh_failed');
});

test('the cumulative one-second intake flush preserves its partial primary ledger when the sink consumes its remainder', { timeout: 1000 }, async () => {
  const identities = new Map([[10, owner]]); const first = child(11); const second = child(13); let now = 1000; let refreshes = 0;
  const failures = await captureOwnedCandidateSnapshot([owner, first, second], identities, [], { ...options, clock: () => now,
    discover: (_snapshot, values) => { values.set(11, first); values.set(13, second); },
    refreshSnapshot: async () => { refreshes += 1; return []; }, recordEvent: () => { now += 1000; } });
  assert.equal(identities.get(11).commandLine, 'owned child'); assert.equal(identities.has(13), false);
  assert.equal(failures[0].subreason, 'candidate_snapshot_deadline'); assert.equal(refreshes, 0);
});

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
  assert.deepEqual(failures, [{ stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command', chainIndex: null,
    structure: { candidatePid: 11, candidateParentPid: 10, ownerPid: 10, parentChainCount: 2, expectedParentPid: null, observedParentPid: null } }]);
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

test('own tuple rejections identify a fixed guard without admitting any candidate', { timeout: 1000 }, () => {
  const cases = [
    [null, 'identity_missing', 'identity'],
    [{ ...child(11), pid: 0 }, 'identity_pid_invalid', 'pid'],
    [{ ...child(11), parentPid: -1 }, 'identity_parent_pid_invalid', 'parentPid'],
    [{ ...child(11), created: null }, 'identity_created_missing', 'created'],
    [{ ...child(11), created: 'invalid synthetic creation' }, 'identity_created_invalid', 'created'],
    [{ ...child(11), commandLine: undefined }, 'identity_command_missing', 'commandLine'],
    [{ ...child(11), commandLine: {} }, 'identity_command_invalid', 'commandLine'],
    [{ ...child(11), commandLine: '' }, 'identity_command_empty', 'commandLine'],
    [{ ...child(11), commandLine: 'x'.repeat(131073) }, 'identity_command_length_exceeded', 'commandLine'],
    [{ ...child(11), parentChain: null }, 'parent_chain_missing', 'parentChain'],
    [{ ...child(11), parentChain: [] }, 'parent_chain_empty', 'parentChain'],
    [{ ...child(11), parentChain: Array.from({ length: 13 }, () => ({})) }, 'parent_chain_length_exceeded', 'parentChain'],
  ];
  const expires = Date.now() + 800;
  assert.ok(cases.length <= 12);
  for (let index = 0; index < cases.length && index < 12; index += 1) {
    assert.ok(Date.now() < expires);
    const [value, subreason, failedField] = cases[index]; const identities = new Map([[10, owner]]);
    assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' }), (error) => {
      const failure = ownedIdentityFailure(error, value?.pid);
      assert.equal(failure.subreason, subreason); assert.equal(failure.failedField, failedField);
      assert.equal(failure.chainIndex, null); return true;
    });
    assert.equal(identities.size, 1);
  }
});

test('parent tuple rejections retain only the guard and bounded chain coordinates', { timeout: 1000 }, () => {
  const cases = [
    [null, 'parent_identity_missing', 'parentChain.identity'],
    [{ pid: 90 }, 'parent_chain_discontinuous', 'parentChain.pid'],
    [{ pid: 10, unavailable: true }, 'parent_unavailable_not_terminal', 'parentChain.unavailable'],
    [{ ...child(11).parentChain[0], parentPid: -1 }, 'parent_parent_pid_invalid', 'parentChain.parentPid'],
    [{ ...child(11).parentChain[0], created: null }, 'parent_created_missing', 'parentChain.created'],
    [{ ...child(11).parentChain[0], created: 'invalid synthetic creation' }, 'parent_created_invalid', 'parentChain.created'],
    [{ ...child(11).parentChain[0], commandLine: null }, 'parent_command_missing', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: 123 }, 'parent_command_invalid', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: '' }, 'parent_command_empty', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], commandLine: 'x'.repeat(131073) }, 'parent_command_length_exceeded', 'parentChain.commandLine'],
    [{ ...child(11).parentChain[0], created: '2026-10-07T00:01:00.000Z' }, 'parent_created_after_identity', 'parentChain.created'],
  ];
  const expires = Date.now() + 800;
  assert.ok(cases.length <= 12);
  for (let index = 0; index < cases.length && index < 12; index += 1) {
    assert.ok(Date.now() < expires);
    const [parent, subreason, failedField] = cases[index]; const identities = new Map([[10, owner]]);
    const value = { ...child(11), parentChain: [parent, child(11).parentChain[1]] };
    assert.throws(() => acceptOwnedIdentity(value, identities, { ...options, eventName: 'helper' }), (error) => {
      const failure = ownedIdentityFailure(error, 11);
      assert.equal(failure.subreason, subreason); assert.equal(failure.failedField, failedField);
      assert.equal(failure.chainIndex, 0); assert.equal(failure.structure.expectedParentPid, 10);
      assert.equal(failure.structure.observedParentPid, parent?.pid ?? null); return true;
    });
    assert.equal(identities.has(11), false);
  }
});

test('cycle and absent exact anchor remain rejections with separate fixed reasons', { timeout: 1000 }, () => {
  const cyclic = { ...child(11), parentPid: 12, parentChain: [
    { pid: 12, parentPid: 11, created, commandLine: 'intermediate' },
    { pid: 11, parentPid: 10, created, commandLine: 'owned child' },
  ] };
  assert.throws(() => validateOwnedIdentity(cyclic, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_cycle'); assert.equal(failure.chainIndex, 1); return true;
  });
  assert.throws(() => validateOwnedIdentity({ ...child(11), parentPid: 90, parentChain: [{ pid: 90, unavailable: true }] }, 10), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'ownership_anchor_not_reached'); assert.equal(failure.completeness, 'missing_anchor'); return true;
  });
  assert.throws(() => validateOwnedIdentityAnchor(child(11), { ...owner, commandLine: 'changed owner' }), (error) => {
    assert.equal(ownedIdentityFailure(error, 11).subreason, 'ownership_anchor_tuple_mismatch'); return true;
  });
});

test('the original one second parent deadline is deterministic and never admits a candidate', { timeout: 1000 }, () => {
  let calls = 0; const identities = new Map([[10, owner]]);
  assert.throws(() => acceptOwnedIdentity(child(11), identities, { ...options, clock: () => calls++ === 0 ? 1000 : 2000, eventName: 'helper' }), (error) => {
    const failure = ownedIdentityFailure(error, 11);
    assert.equal(failure.subreason, 'parent_chain_deadline'); assert.equal(failure.chainIndex, 0);
    assert.equal(failure.completeness, 'deadline'); return true;
  });
  assert.equal(calls, 2); assert.equal(identities.has(11), false);
});

test('owner ledger revalidation keeps its distinct bounded deadline failure', { timeout: 1000 }, () => {
  const identities = new Map([[11, child(11)]]); let calls = 0;
  assert.throws(() => acceptOwnedIdentity(owner, identities, { ...options, clock: () => ++calls < 3 ? 1000 : 2000, eventName: 'runner' }), (error) => {
    const failure = ownedIdentityFailure(error, 10);
    assert.equal(failure.reason, 'ownership_anchor_changed_preserved');
    assert.equal(failure.subreason, 'ownership_scan_deadline'); return true;
  });
  assert.equal(calls, 3); assert.equal(identities.has(10), false);
});

test('malicious failure metadata is rebuilt from fixed labels and UInt32 numeric coordinates', { timeout: 1000 }, () => {
  const marker = 'SYNTHETIC secret raw error must be omitted';
  const failure = ownedIdentityFailure({ message: marker, identityEvidenceFailure: {
    stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved', subreason: 'identity_command_missing',
    failedField: 'commandLine', completeness: 'missing_command', chainIndex: 1, raw: marker,
    structure: { candidatePid: marker, candidateParentPid: 0x100000000, ownerPid: 10, parentChainCount: 4097,
      expectedParentPid: -1, observedParentPid: NaN, commandLine: marker },
  } }, 0x100000000);
  assert.deepEqual(failure, { stage: 'identity', pid: null, reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command', chainIndex: 1,
    structure: { candidatePid: null, candidateParentPid: null, ownerPid: 10, parentChainCount: null, expectedParentPid: null, observedParentPid: null } });
  assert.equal(JSON.stringify(failure).includes(marker), false);
  const spoofed = ownedIdentityFailure({ identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    subreason: 'identity_command_missing', failedField: marker, completeness: 'missing_command', raw: marker } }, 11);
  assert.deepEqual(spoofed, { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure(new Error(marker), 11), { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: { stage: 'event', reason: 'secondary_event_failed',
    subreason: 'identity_command_missing', failedField: 'commandLine', completeness: 'missing_command' } }, 11),
  { stage: 'event', pid: 11, reason: 'secondary_event_failed' });
});

test('numeric projection limits do not change the original safe-integer identity admission', { timeout: 1000 }, () => {
  const largePid = 0x100000000; const value = { ...child(largePid), parentPid: 10 };
  assert.doesNotThrow(() => validateOwnedIdentity(value, 10));
  assert.throws(() => validateOwnedIdentity({ ...value, commandLine: null }, 10), (error) => {
    const failure = ownedIdentityFailure(error, largePid);
    assert.equal(failure.pid, null); assert.equal(failure.structure.candidatePid, null); return true;
  });
});

test('changing or throwing fake-error getters cannot bypass the safe projection', { timeout: 1000 }, () => {
  const marker = 'SYNTHETIC getter secret must never persist';
  const reads = { stage: 0, reason: 0, subreason: 0, chainIndex: 0, parentChainCount: 0 };
  const detail = { failedField: 'commandLine', completeness: 'missing_command',
    get stage() { return ++reads.stage === 1 ? 'identity' : marker; },
    get reason() { return ++reads.reason === 1 ? 'incomplete_or_unsafe_identity_preserved' : marker; },
    get subreason() { return ++reads.subreason === 1 ? 'identity_command_missing' : marker; },
    get chainIndex() { return ++reads.chainIndex === 1 ? 0 : marker; },
    structure: { candidatePid: 11, candidateParentPid: 10, ownerPid: 10, expectedParentPid: 10, observedParentPid: 10,
      get parentChainCount() { return ++reads.parentChainCount === 1 ? 2 : marker; } },
  };
  const projected = ownedIdentityFailure({ identityEvidenceFailure: detail }, 11);
  assert.equal(JSON.stringify(projected).includes(marker), false);
  assert.deepEqual(reads, { stage: 1, reason: 1, subreason: 1, chainIndex: 1, parentChainCount: 1 });
  assert.equal(projected.chainIndex, 0); assert.equal(projected.structure.parentChainCount, 2);
  assert.deepEqual(ownedIdentityFailure({ get identityEvidenceFailure() { throw new Error(marker); } }, 11),
    { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
  assert.deepEqual(ownedIdentityFailure({ identityEvidenceFailure: { stage: 'identity', reason: 'incomplete_or_unsafe_identity_preserved',
    get subreason() { throw new Error(marker); } } }, 11),
  { stage: 'identity', pid: 11, reason: 'incomplete_or_unsafe_identity_preserved' });
});

test('credential variants cannot enter a persisted rejection or become cleanup authority', { timeout: 2500 }, async (context) => {
  const marker = 'WB45_SYNTHETIC_CREDENTIAL_/+never-persist!';
  const variants = [marker, Buffer.from(marker).toString('base64'), Buffer.from(marker).toString('base64url'),
    Buffer.from(marker).toString('hex'), Buffer.from(marker).toString('hex').toUpperCase(), encodeURIComponent(marker)];
  const directory = await mkdtemp(path.join(tmpdir(), 'sonnetdb-wb45-identity-test-'));
  let canonical; let file; let ownedDirectoryVerified = false; let directoryRemoved = false;
  try {
    canonical = await realpath(directory); const canonicalTemp = await realpath(tmpdir());
    file = path.join(canonical, 'safe-rejection.json');
    assert.equal(path.dirname(canonical), canonicalTemp); assert.ok(path.basename(canonical).startsWith('sonnetdb-wb45-identity-test-'));
    ownedDirectoryVerified = true;
    const expires = Date.now() + 1500;
    assert.equal(variants.length, 6);
    for (let index = 0; index < variants.length && index < 6; index += 1) {
      assert.ok(Date.now() < expires); const identities = new Map([[10, owner]]); const events = []; let rejection;
      assert.throws(() => acceptOwnedIdentity({ ...child(11), parentChain: [...child(11).parentChain.slice(0, 1),
        { pid: 1, parentPid: 0, created, commandLine: variants[index] }] }, identities, { ...options,
        eventName: 'helper', recordEvent: (value) => events.push(value), validateText: (text) => {
          if (variants.some((variant) => text.includes(variant))) throw new Error(marker);
        } }), (error) => { rejection = ownedIdentityFailure(error, 11); return true; });
      assert.equal(rejection.reason, 'unsafe_identity_text_preserved'); assert.equal(identities.has(11), false); assert.equal(events.length, 0);
      await writeFile(file, JSON.stringify({ rejection, identities: [...identities.values()], events }), 'utf8');
      const persisted = await readFile(file, 'utf8');
      assert.equal(variants.some((variant) => persisted.includes(variant)), false);
    }
  } finally {
    try {
      if (ownedDirectoryVerified) {
        await unlink(file).catch((error) => { if (error.code !== 'ENOENT') throw error; });
        await rmdir(canonical); directoryRemoved = true;
      }
    } finally {
      context.diagnostic(`WB45 synthetic credential temporary directory ${directoryRemoved ? 'removed' : 'retained; ownership/cleanup verification incomplete'}: ${canonical ?? directory}`);
    }
  }
});
