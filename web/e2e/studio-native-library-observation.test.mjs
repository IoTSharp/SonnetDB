import assert from 'node:assert/strict';
import test from 'node:test';
import { observeLibraryFieldCategories, projectObservedDatabaseDiskSnapshot } from './studio-native-library-observation.mjs';
import { projectDatabaseSnapshot } from './studio-native-database-scenario.mjs';
import { encodeNativeEvidence, persistNativeTerminalEvidence } from './studio-native-evidence.mjs';

const fixture = () => ({ profiles: [{ id: 'managed-local', name: 'secret-name', kind: 'managed-local',
  baseUrl: 'https://secret-host.invalid', defaultDatabase: 'secret-database', tokenMode: 'current-session', createdAt: 1, updatedAt: 2 }],
activeProfileId: 'managed-local', activeDatabase: 'secret-database' });
const observe = (value) => observeLibraryFieldCategories(value, { now: () => 0 });

test('ordinary disk shape emits field names only and preserves the strict projection', { timeout: 1000 }, () => {
  const value = fixture(); const before = structuredClone(value); const observations = [];
  const actual = projectObservedDatabaseDiskSnapshot(value, (entry) => observations.push(entry));
  assert.deepEqual(actual, projectDatabaseSnapshot(before, { disk: true }));
  assert.deepEqual(value, before);
  assert.equal(observations.length, 1); assert.equal(observations[0].state, 'observed');
  assert.deepEqual(observations[0].library.storedFields, ['profiles', 'activeProfileId', 'activeDatabase']);
  assert.equal(observations[0].profile.storedFields.length, 8);
  assert.equal(observations[0].profileCount, 1);
  assert.doesNotMatch(JSON.stringify(observations), /secret-|managed-local|current-session/u);
});

test('known derived fields are observable while the original disk refusal remains', { timeout: 1000 }, () => {
  const value = fixture(); value.activeIdentity = { secret: 'secret-derived' }; value.profiles[0].identity = { secret: 'secret-derived' };
  let observed;
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, (entry) => { observed = entry; }), /unexpected library fields/u);
  assert.deepEqual(observed.library.derivedFields, ['activeIdentity']);
  assert.deepEqual(observed.profile.derivedFields, ['identity']);
  assert.equal(observed.library.unknownFieldCount, 0); assert.equal(observed.profile.unknownFieldCount, 0);
  assert.doesNotMatch(JSON.stringify(observed), /secret/u);
  delete value.activeIdentity;
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, () => {}), /unexpected profile fields/u);
});

test('unknown sensitive property names and values are counted without being retained', { timeout: 1000 }, () => {
  const value = fixture(); value['secret-token-in-key'] = 'secret-token-value';
  value.profiles[0]['secret-password-in-key'] = { nested: 'secret-password-value' };
  Object.defineProperty(value, 'secret-nonenumerable', { value: 'secret-value' });
  value[Symbol('secret-symbol')] = 'secret-symbol-value';
  const observed = observe(value);
  assert.equal(observed.library.unknownFieldCount, 3); assert.equal(observed.profile.unknownFieldCount, 1);
  assert.doesNotMatch(JSON.stringify(observed), /secret|password|token-in-key|nonenumerable|symbol/u);
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, () => {}), /unexpected library fields/u);
});

test('field values and nested derived identities are never read by the observer', { timeout: 1000 }, () => {
  let reads = 0; const value = fixture();
  for (const [target, key] of [[value, 'activeDatabase'], [value, 'activeIdentity'], [value.profiles[0], 'identity']]) {
    Object.defineProperty(target, key, { enumerable: true, get() { reads += 1; throw new Error('secret-getter'); } });
  }
  const observed = observe(value);
  assert.equal(reads, 0); assert.equal(observed.state, 'observed');
  assert.deepEqual(observed.library.derivedFields, ['activeIdentity']);
  assert.deepEqual(observed.profile.derivedFields, ['identity']);
});

test('profiles and array slot accessors are refused without invocation', { timeout: 1000 }, () => {
  let reads = 0;
  const value = { get profiles() { reads += 1; throw new Error('secret'); } };
  const first = observe(value); assert.equal(first.state, 'partial'); assert.equal(first.profile.state, 'invalid-profiles');
  const slots = []; slots.length = 1;
  Object.defineProperty(slots, '0', { get() { reads += 1; throw new Error('secret'); } });
  const second = observe({ profiles: slots }); assert.equal(second.profile.state, 'invalid-profile');
  assert.equal(reads, 0);
});

test('observation and callback faults preserve the exact primary exception object', { timeout: 1000 }, () => {
  const primary = new Error('original strict getter failure');
  const value = { get profiles() { throw primary; } };
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, () => { throw new Error('record fault'); }), (error) => error === primary);
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, () => {}, { now: () => { throw new Error('clock fault'); } }), (error) => error === primary);
});

test('recording failure cannot turn an accepted legacy snapshot into a failure', { timeout: 1000 }, () => {
  const value = fixture();
  assert.deepEqual(projectObservedDatabaseDiskSnapshot(value, () => { throw new Error('record fault'); }), projectDatabaseSnapshot(value, { disk: true }));
});

test('invalid object kinds and hostile observation traps remain fixed unknown states', { timeout: 1000 }, () => {
  for (const value of [null, [], 'secret-string', 5, new Date(0), new Proxy({}, { ownKeys() { throw new Error('secret-trap'); } })]) {
    const result = observe(value); assert.equal(result.state, 'unknown'); assert.doesNotMatch(JSON.stringify(result), /secret/u);
  }
  const value = Object.assign(Object.create(null), fixture());
  assert.equal(observe(value).state, 'observed');
});

test('exactly 32 fields per object are observed and 33 are capped', { timeout: 1000 }, () => {
  const value = fixture();
  for (let i = 0; i < 29; i += 1) value[`secret-root-${i}`] = 'secret';
  for (let i = 0; i < 24; i += 1) value.profiles[0][`secret-profile-${i}`] = 'secret';
  const exact = observe(value); assert.equal(exact.state, 'observed');
  assert.equal(exact.library.unknownFieldCount, 29); assert.equal(exact.profile.unknownFieldCount, 24);
  value['secret-extra'] = 'secret'; assert.equal(observe(value).library.state, 'field-cap');
  delete value['secret-extra']; value.profiles[0]['secret-extra'] = 'secret';
  assert.equal(observe(value).profile.state, 'field-cap');
  assert.doesNotMatch(JSON.stringify(observe(value)), /secret/u);
});

test('only a unique profile is inspected and oversized collections do not report a false count', { timeout: 1000 }, () => {
  for (const count of [0, 2, 32, 33]) {
    const result = observe({ profiles: Array(count).fill({ secret: 'secret-value' }) });
    assert.equal(result.state, 'partial');
    assert.equal(result.profileCount, count <= 32 ? count : null);
    assert.equal(result.profile.state, count <= 32 ? 'profile-count' : 'profile-cap');
    assert.equal(result.profile.unknownFieldCount, null);
    assert.doesNotMatch(JSON.stringify(result), /secret/u);
  }
});

test('cancellation and monotonic 100ms deadline fail closed without replacing validation', { timeout: 1000 }, () => {
  const value = fixture(); const controller = new AbortController(); controller.abort();
  assert.equal(observeLibraryFieldCategories(value, { signal: controller.signal }).state, 'unknown');
  for (const times of [[0, 100], [1, 0], [0, Number.NaN]]) {
    let reads = 0; const now = () => times[Math.min(reads++, 1)];
    assert.equal(observeLibraryFieldCategories(value, { now }).state, 'unknown'); assert.equal(reads, 2);
  }
  assert.deepEqual(projectObservedDatabaseDiskSnapshot(value, () => {}, { signal: controller.signal }), projectDatabaseSnapshot(value, { disk: true }));
});

test('field observation never relaxes known derived, unknown-field or profile-count guards', { timeout: 1000 }, () => {
  for (const change of [(value) => { value.activeIdentity = {}; }, (value) => { value.secret = 'secret'; },
    (value) => { value.profiles[0].identity = {}; }, (value) => { value.profiles = []; }]) {
    const value = fixture(); change(value); let before;
    try { projectDatabaseSnapshot(value, { disk: true }); } catch (error) { before = error.message; }
    assert.equal(typeof before, 'string');
    assert.throws(() => projectObservedDatabaseDiskSnapshot(value, () => {}), (error) => error.message === before);
  }
});

test('a clock regression after successful checks also stops observation', { timeout: 1000 }, () => {
  const times = [0, 1, 2, 1]; let reads = 0;
  const result = observeLibraryFieldCategories(fixture(), { now: () => times[Math.min(reads++, 3)] });
  assert.equal(result.state, 'unknown'); assert.equal(reads, 4);
});

test('safe categories survive the existing terminal writer while the failed journey stays failed', { timeout: 1000 }, async () => {
  const value = fixture(); value['secret-token-in-key'] = 'secret-token-value'; value.activeIdentity = { token: 'secret-token-value' };
  let observation;
  assert.throws(() => projectObservedDatabaseDiskSnapshot(value, (entry) => { observation = entry; }));
  const recovery = { passed: false, selections: [], restored: null, libraryFieldObservations: [observation] };
  const writes = new Map();
  const terminal = await persistNativeTerminalEvidence({
    write: async (name, payload) => { writes.set(name, encodeNativeEvidence(payload, ['secret-token-value'])); },
    normalExit: { normalExit: false }, cleanup: { cleanupProven: false },
    result: { passed: false, fatal: { stage: 'disk', message: 'Original refusal.' }, databaseRecovery: recovery },
    details: [{ name: 'database-recovery.json', value: recovery }], deadline: Date.now() + 5000,
  });
  assert.equal(terminal.passed, false); assert.equal(terminal.outcomes.every((entry) => entry.persisted), true);
  assert.equal(writes.size, 4);
  const saved = JSON.parse(writes.get('database-recovery.json'));
  assert.deepEqual(saved.libraryFieldObservations, [observation]); assert.equal(saved.passed, false);
  assert.equal(JSON.parse(writes.get('result.json')).databaseRecovery.restored, null);
  assert.doesNotMatch([...writes.values()].join(''), /secret-token/u);
});
