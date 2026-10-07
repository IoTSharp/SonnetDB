import assert from 'node:assert/strict';
import test from 'node:test';
import { admitNativeStudioRoot, assertDatabaseAcknowledgement, assertDatabaseClose, assertDatabaseSnapshot, compactDatabaseProcessEvidence, createDatabaseRecoveryContract,
  projectDatabaseSnapshot, readDatabaseSqlResult } from './studio-native-database-scenario.mjs';

const origin = 'http://127.0.0.1:18338';
const databaseA = 'WB61_Alpha_Test';
const databaseB = 'WB61_Bravo_Test';
const identity = (database) => ({ host: 'studio-desktop', profileId: 'managed-local', baseUrl: origin, database });
const snapshot = (database, disk = false) => ({ activeProfileId: 'managed-local', activeDatabase: database,
  profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: origin, defaultDatabase: database,
    tokenMode: 'current-session', createdAt: 1, updatedAt: 2, ...(disk ? {} : { identity: identity(database) }) }],
  ...(disk ? {} : { activeIdentity: identity(database) }) });
const ack = (database, sequence, launch = 1, method = 'PUT') => ({ sequence, requestSequence: sequence, launch, method, path: '/studio-bridge/connections',
  httpStatus: 200, body: projectDatabaseSnapshot(snapshot(database)) });
const dom = (database) => ({ activeDatabase: database, hostIdentity: `studio-desktop · managed-local · ${origin} · ${database}`, contractWarning: false });
const disk = (database) => projectDatabaseSnapshot(snapshot(database, true), { disk: true });
const close = () => ({ method: 'CloseMainWindow', accepted: true, exitCode: 0, exitSignal: null, studioIdentityExited: true,
  serverIdentityExited: true, allOwnedIdentitiesExited: true, allFourPortsReleased: true, fallbackUsed: false });
const query = () => ({ database: databaseB, requestPath: `/v1/db/${databaseB}/sql`, method: 'POST', httpStatus: 200,
  requestSql: 'SELECT "Marker" FROM "WB61Probe"', sentinelA: 'WB61_A', sentinelB: 'WB61_B', rendered: 'WB61_B',
  actual: { columns: ['Marker'], rows: [['WB61_B']], rowCount: 1, complete: true } });
const contract = () => createDatabaseRecoveryContract({ databaseA, databaseB, origin });
const barrier = (sequence) => ({ afterSequence: sequence, afterRequestSequence: sequence });
const selectedBoth = () => {
  const value = contract(); value.launched('100:creation-A');
  value.selected(databaseA, ack(databaseA, 3), dom(databaseA), disk(databaseA), barrier(2));
  value.selected(databaseB, ack(databaseB, 5), dom(databaseB), disk(databaseB), barrier(4));
  return value;
};
const restored = () => {
  const value = selectedBoth(); value.firstClosed(close()); value.relaunched('101:creation-B');
  value.restored(ack(databaseB, 9, 2, 'GET'), dom(databaseB), disk(databaseB), barrier(8)); return value;
};

test('acceptance requires the complete A to B, actual second launch, B query and both strict closes', () => {
  const value = restored(); assert.equal(value.passed, false); value.queried(query()); assert.equal(value.passed, false);
  value.secondClosed(close()); assert.equal(value.passed, true);
});

test('a stale, absent or wrong-launch PUT cannot certify a selection', () => {
  const expected = { ...barrier(3), launch: 1, method: 'PUT', database: databaseA, origin };
  for (const value of [null, ack(databaseA, 3), ack(databaseA, 4, 2), ack(databaseA, 4, 1, 'GET'), { ...ack(databaseA, 4), httpStatus: 500 }]) {
    assert.throws(() => assertDatabaseAcknowledgement(value, expected), /acknowledgement/u);
  }
});

test('wrong database, case folding, profile default and partial identity all refuse acceptance', () => {
  const valid = projectDatabaseSnapshot(snapshot(databaseB));
  for (const mutate of [
    (value) => { value.activeDatabase = databaseA; },
    (value) => { value.activeDatabase = databaseB.toLowerCase(); },
    (value) => { value.profiles[0].defaultDatabase = databaseA; },
    (value) => { value.profiles[0].identity.database = databaseA; },
    (value) => { value.activeIdentity.baseUrl = 'http://127.0.0.1:9999'; },
    (value) => { delete value.activeIdentity; },
  ]) {
    const value = structuredClone(valid); mutate(value); assert.throws(() => assertDatabaseSnapshot(value, databaseB, origin));
  }
  assert.throws(() => assertDatabaseSnapshot(valid, '__control_plane__', origin));
});

test('disk semantics do not fabricate native identities and secret or unknown fields are refused', () => {
  const value = disk(databaseB); assert.equal(assertDatabaseSnapshot(value, databaseB, origin, { disk: true }), true);
  assert.equal('activeIdentity' in value, false); assert.equal('identity' in value.profiles[0], false);
  assert.equal('name' in value.profiles[0], false); assert.equal('updatedAt' in value.profiles[0], false);
  for (const input of [
    { ...snapshot(databaseB), token: 'never-retain' },
    { ...snapshot(databaseB), authorization: 'never-retain' },
    { ...snapshot(databaseB), profiles: [{ ...snapshot(databaseB).profiles[0], password: 'never-retain' }] },
    { ...snapshot(databaseB), activeIdentity: { ...identity(databaseB), token: 'never-retain' } },
  ]) assert.throws(() => projectDatabaseSnapshot(input));
  assert.throws(() => projectDatabaseSnapshot(snapshot(databaseB), { disk: true }));
});

test('B first, repeated A, mismatched DOM and reused sequence do not advance the journey', () => {
  const first = contract(); first.launched('100:creation-A');
  assert.throws(() => first.selected(databaseB, ack(databaseB, 2), dom(databaseB), disk(databaseB), barrier(1)), /order/u);
  assert.throws(() => first.selected(databaseA, ack(databaseA, 2), dom(databaseB), disk(databaseA), barrier(1)), /rendered/u);
  first.selected(databaseA, ack(databaseA, 3), dom(databaseA), disk(databaseA), barrier(2));
  assert.throws(() => first.selected(databaseA, ack(databaseA, 5), dom(databaseA), disk(databaseA), barrier(4)), /order/u);
  assert.throws(() => first.selected(databaseB, ack(databaseB, 5), dom(databaseB), disk(databaseB), barrier(2)), /previous acknowledgement/u);
  assert.equal(first.passed, false);
});

test('page reload or missing first strict close cannot substitute for a second native launch', () => {
  const first = selectedBoth(); assert.throws(() => first.relaunched('101:creation-B'), /order/u);
  first.firstClosed(close()); assert.throws(() => first.relaunched('100:creation-A'), /reload/u);
  first.relaunched('101:creation-B'); assert.equal(first.passed, false);
});

test('fallback, unknown exit, surviving child or occupied port refuses every close', () => {
  for (const invalid of [
    { ...close(), fallbackUsed: true }, { ...close(), fallbackUsed: undefined }, { ...close(), accepted: false },
    { ...close(), exitCode: null }, { ...close(), exitSignal: 'SIGTERM' }, { ...close(), allOwnedIdentitiesExited: false },
    { ...close(), allFourPortsReleased: false }, { ...close(), serverIdentityExited: false },
  ]) assert.throws(() => assertDatabaseClose(invalid), /strict absence/u);
});

test('second launch requires a fresh GET plus original B DOM and persisted default', () => {
  for (const [response, rendered, library, after] of [
    [ack(databaseB, 9, 1, 'GET'), dom(databaseB), disk(databaseB), 8],
    [ack(databaseB, 9, 2, 'PUT'), dom(databaseB), disk(databaseB), 8],
    [ack(databaseB, 9, 2, 'GET'), dom(databaseB), disk(databaseB), 9],
    [ack(databaseA, 9, 2, 'GET'), dom(databaseB), disk(databaseB), 8],
    [ack(databaseB, 9, 2, 'GET'), dom(databaseA), disk(databaseB), 8],
    [ack(databaseB, 9, 2, 'GET'), dom(databaseB), disk(databaseA), 8],
  ]) {
    const value = selectedBoth(); value.firstClosed(close()); value.relaunched('101:creation-B');
    assert.throws(() => value.restored(response, rendered, library, barrier(after))); assert.equal(value.passed, false);
  }
});

test('a response from A, incomplete result or mismatched DOM cannot certify the recovered B query', () => {
  for (const invalid of [
    { ...query(), requestPath: `/v1/db/${databaseA}/sql` }, { ...query(), httpStatus: 500 },
    { ...query(), requestSql: 'SELECT 1' }, { ...query(), rendered: 'WB61_A' },
    { ...query(), actual: { rows: [['WB61_A']], complete: true } },
    { ...query(), actual: { rows: [['WB61_B']], complete: false } }, { ...query(), sentinelA: 'WB61_B' },
  ]) { const value = restored(); assert.throws(() => value.queried(invalid)); assert.equal(value.passed, false); }
});

test('real SQL fixture accepts native elapsed metadata and refuses missing end, wrong rows or SQL error', () => {
  const raw = (row, end = { type: 'end', rowCount: 1, recordsAffected: -1, elapsedMilliseconds: 1 }) =>
    [JSON.stringify({ type: 'meta', columns: ['Marker'] }), JSON.stringify([row]), JSON.stringify(end)].join('\n');
  assert.deepEqual(readDatabaseSqlResult(raw('WB61_B'), 'WB61_B').rows, [['WB61_B']]);
  assert.throws(() => readDatabaseSqlResult(raw('WB61_A'), 'WB61_B'));
  assert.throws(() => readDatabaseSqlResult(raw('WB61_B', { type: 'error', message: 'not accepted' }), 'WB61_B'));
  assert.throws(() => readDatabaseSqlResult(raw('WB61_B', { type: 'end', rowCount: 0, elapsedMilliseconds: 1 }), 'WB61_B'));
  assert.throws(() => readDatabaseSqlResult(raw('WB61_B', { type: 'end', rowCount: 1, elapsedMilliseconds: -1 }), 'WB61_B'));
  assert.throws(() => readDatabaseSqlResult('{"type":"meta","columns":["Marker"]}\n["WB61_B"]', 'WB61_B'));
});

test('96 helper records retain every identity through bounded legacy compactor segments with events once', () => {
  for (const count of [0, 32, 64, 65, 96]) {
    const helpers = Array.from({ length: count }, (_, index) => ({ processId: 1000 + index, identityKey: `${1000 + index}:creation` }));
    const calls = [];
    const compact = (input) => {
      calls.push(input); assert.ok(input.helpers.length <= 64);
      return { helpers: input.helpers, events: input.events, identities: input.helpers.map((helper) => ({ identityKey: helper.identityKey })) };
    };
    const value = compactDatabaseProcessEvidence(compact, { helpers, events: [{ event: 'native-exit' }] });
    assert.deepEqual(value.segments.flatMap((segment) => segment.evidence.helpers), helpers);
    assert.equal(calls.flatMap((call) => call.events).length, 1); assert.ok(calls.length <= 2); assert.equal(value.complete, true);
  }
});

test('process evidence refuses overflow or a compactor omitting helpers instead of claiming partial completeness', () => {
  const identity = (value) => ({ helpers: value.helpers, events: value.events });
  assert.throws(() => compactDatabaseProcessEvidence(identity, { helpers: Array.from({ length: 97 }, () => ({})), events: [] }));
  assert.throws(() => compactDatabaseProcessEvidence(identity, { helpers: [], events: Array.from({ length: 25 }, () => ({})) }));
  assert.throws(() => compactDatabaseProcessEvidence(() => ({ helpers: [], events: [] }), { helpers: [{}], events: [] }), /omitted/u);
});

test('late JSON decode and a pre-action request with a late response cannot cross both observation barriers', () => {
  const expected = { afterSequence: 20, afterRequestSequence: 100, launch: 2, method: 'GET', database: databaseB, origin };
  const lateDecode = { ...ack(databaseB, 20, 2, 'GET'), requestSequence: 101 };
  const preNavigationRequest = { ...ack(databaseB, 21, 2, 'GET'), requestSequence: 100 };
  assert.throws(() => assertDatabaseAcknowledgement(lateDecode, expected), /acknowledgement/u);
  assert.throws(() => assertDatabaseAcknowledgement(preNavigationRequest, expected), /acknowledgement/u);
  assert.throws(() => assertDatabaseAcknowledgement({ ...preNavigationRequest, requestSequence: null }, expected), /acknowledgement/u);
  assert.equal(assertDatabaseAcknowledgement({ ...preNavigationRequest, requestSequence: 101 }, expected), true);
});

test('a rejected native root candidate cannot publish cleanup authority, including when the owned map is full', () => {
  const runnerIdentity = { processId: 10, parentProcessId: 9, creationTimeUtc: '2026-10-07T14:00:00Z', commandLine: 'node runner', executablePath: 'C:\\node.exe' };
  const candidate = { processId: 20, parentProcessId: 10, creationTimeUtc: '2026-10-07T14:00:01Z',
    commandLine: 'Studio.exe --data-root D:\\owned-data', executablePath: 'C:\\Studio.exe', parentChain: [runnerIdentity] };
  const expected = { processId: 20, parentProcessId: 10, executablePath: 'C:\\Studio.exe', dataRoot: 'D:\\owned-data',
    runnerIdentity, ownedIdentityKeys: [], maximumOwned: 32 };
  for (const [root, options] of [
    [{ ...candidate, parentProcessId: 30 }, expected], [{ ...candidate, processId: 21 }, expected],
    [{ ...candidate, executablePath: 'C:\\Foreign.exe' }, expected], [{ ...candidate, commandLine: 'Studio.exe --data-root D:\\foreign' }, expected],
    [{ ...candidate, parentChain: [{ ...runnerIdentity, creationTimeUtc: '2026-10-07T13:59:00Z' }] }, expected],
    [candidate, { ...expected, ownedIdentityKeys: Array.from({ length: 32 }, (_, index) => `${index}:other`) }],
  ]) {
    const cleanupAuthority = new Map(); let currentRoot;
    assert.throws(() => admitNativeStudioRoot(root, options, (admitted) => { currentRoot = admitted; cleanupAuthority.set(admitted.processId, admitted); }));
    assert.equal(currentRoot, undefined); assert.equal(cleanupAuthority.size, 0);
  }
  let currentRoot;
  admitNativeStudioRoot(candidate, expected, (admitted) => { currentRoot = admitted; }); assert.equal(currentRoot, candidate);
});
