import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { admitNativeStudioRoot, assertDatabaseAcknowledgement, assertDatabaseClose, assertDatabaseSnapshot, compactDatabaseProcessEvidence, createDatabaseRecoveryContract,
  beginDatabaseSelectionAttempt, observeDatabaseSeedFailure, projectDatabaseSelectionCandidates, projectDatabaseSelectionDom, projectDatabaseSnapshot,
  readDatabaseSqlResult, rejectDatabaseSeedHttpFailure, rejectDatabaseSelectionFailure, runDatabaseRecoveryScenario } from './studio-native-database-scenario.mjs';

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
const disk = (database) => projectDatabaseSnapshot(snapshot(database), { disk: true });
const serializedLibrary = () => JSON.parse(readFileSync(new URL('./fixtures/studio-managed-local-library.json', import.meta.url), 'utf8'));
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

test('disk semantics do not fabricate native identities and secret or unknown fields are refused', { timeout: 1000 }, () => {
  const expires = Date.now() + 500;
  const value = disk(databaseB); assert.equal(assertDatabaseSnapshot(value, databaseB, origin, { disk: true }), true);
  assert.equal('activeIdentity' in value, false); assert.equal('identity' in value.profiles[0], false);
  assert.equal('name' in value.profiles[0], false); assert.equal('updatedAt' in value.profiles[0], false);
  for (const input of [
    { ...snapshot(databaseB), token: 'never-retain' },
    { ...snapshot(databaseB), authorization: 'never-retain' },
    { ...snapshot(databaseB), profiles: [{ ...snapshot(databaseB).profiles[0], password: 'never-retain' }] },
    { ...snapshot(databaseB), activeIdentity: { ...identity(databaseB), token: 'never-retain' } },
    { ...snapshot(databaseB), profiles: [{ ...snapshot(databaseB).profiles[0], identity: { ...identity(databaseB), token: 'never-retain' } }] },
  ]) {
    assert.ok(Date.now() < expires);
    assert.throws(() => projectDatabaseSnapshot(input));
    assert.throws(() => projectDatabaseSnapshot(input, { disk: true }));
  }
  assert.deepEqual(projectDatabaseSnapshot(snapshot(databaseB, true), { disk: true }), value);
});

test('the shared SaveAsync fixture yields only the established minimal disk snapshot', { timeout: 1000 }, () => {
  const input = serializedLibrary(); const before = structuredClone(input);
  const actual = projectDatabaseSnapshot(input, { disk: true });
  assert.deepEqual(actual, { activeProfileId: 'managed-local', activeDatabase: 'MixedCaseDb',
    profiles: [{ id: 'managed-local', kind: 'managed-local', baseUrl: 'http://127.0.0.1:5080',
      defaultDatabase: 'MixedCaseDb', tokenMode: 'current-session' }] });
  assert.equal(assertDatabaseSnapshot(actual, 'MixedCaseDb', 'http://127.0.0.1:5080', { disk: true }), true);
  assert.deepEqual(input, before);
  delete input.activeIdentity; delete input.profiles[0].identity;
  assert.deepEqual(projectDatabaseSnapshot(input, { disk: true }), actual);
});

test('persisted derived identities require a complete pair with exact own fields', { timeout: 1000 }, () => {
  const expires = Date.now() + 500;
  const fixture = serializedLibrary();
  const changes = [
    (value) => { delete value.activeIdentity; },
    (value) => { delete value.profiles[0].identity; },
    (value) => { value.activeIdentity = null; },
    (value) => { value.profiles[0].identity = null; },
    (value) => { value.activeIdentity = []; },
    (value) => { value.profiles[0].identity = 'studio-desktop'; },
    (value) => { delete value.activeIdentity.database; },
    (value) => { delete value.profiles[0].identity.profileId; },
    (value) => { value.activeIdentity.secret = 'never-retain'; },
    (value) => { value.profiles[0].identity.secret = 'never-retain'; },
    (value) => { value.activeIdentity.database = null; },
    (value) => { value.profiles[0].identity.baseUrl = 5080; },
  ];
  for (const change of changes) {
    assert.ok(Date.now() < expires);
    const value = structuredClone(fixture); change(value);
    assert.throws(() => projectDatabaseSnapshot(value, { disk: true }), /identity/u);
  }
});

test('both persisted identities reject foreign and case-folded identity components', { timeout: 1000 }, () => {
  const expires = Date.now() + 500;
  const fixture = serializedLibrary();
  const changes = [
    ['host', 'vscode'], ['host', 'Studio-desktop'],
    ['profileId', 'other-profile'], ['profileId', 'Managed-local'],
    ['baseUrl', 'http://127.0.0.1:9999'], ['baseUrl', 'HTTP://127.0.0.1:5080'],
    ['database', 'OtherDb'], ['database', 'mixedcasedb'],
  ];
  for (const target of ['active', 'profile']) {
    for (const [field, replacement] of changes) {
      assert.ok(Date.now() < expires);
      const value = structuredClone(fixture);
      (target === 'active' ? value.activeIdentity : value.profiles[0].identity)[field] = replacement;
      assert.throws(() => projectDatabaseSnapshot(value, { disk: true }), /derived disk identity/u);
    }
  }
});

test('disk identities bind active selection and profile default to their respective stored values', { timeout: 1000 }, () => {
  const value = serializedLibrary();
  value.activeDatabase = 'SelectedDb'; value.activeIdentity.database = 'SelectedDb';
  const projected = projectDatabaseSnapshot(value, { disk: true });
  assert.equal(projected.activeDatabase, 'SelectedDb'); assert.equal(projected.profiles[0].defaultDatabase, 'MixedCaseDb');
  assert.throws(() => assertDatabaseSnapshot(projected, 'SelectedDb', 'http://127.0.0.1:5080', { disk: true }), /default spelling/u);
  value.activeIdentity.database = 'MixedCaseDb';
  assert.throws(() => projectDatabaseSnapshot(value, { disk: true }), /derived disk identity/u);
  value.activeIdentity.database = 'SelectedDb'; value.profiles[0].identity.database = 'SelectedDb';
  assert.throws(() => projectDatabaseSnapshot(value, { disk: true }), /derived disk identity/u);
  const wrongProfile = serializedLibrary();
  wrongProfile.activeProfileId = 'other-profile'; wrongProfile.activeIdentity.profileId = 'other-profile';
  assert.throws(() => projectDatabaseSnapshot(wrongProfile, { disk: true }), /derived disk identity/u);
});

test('an accepted disk snapshot cannot supply the native acknowledgement identity', { timeout: 1000 }, () => {
  const value = serializedLibrary(); const projected = projectDatabaseSnapshot(value, { disk: true });
  const expected = { ...barrier(1), launch: 1, method: 'PUT', database: 'MixedCaseDb', origin: 'http://127.0.0.1:5080' };
  const receipt = { sequence: 2, requestSequence: 2, launch: 1, method: 'PUT', path: '/studio-bridge/connections', httpStatus: 200 };
  assert.throws(() => assertDatabaseAcknowledgement({ ...receipt, body: projected }, expected), /native identity/u);
  assert.equal(assertDatabaseAcknowledgement({ ...receipt, body: projectDatabaseSnapshot(value) }, expected), true);
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

const failedResponse = (text, status = 400) => ({ status, body: new ReadableStream({ start(controller) {
  controller.enqueue(new TextEncoder().encode(text)); controller.close();
} }) });

test('failed seed observation retains only fixed status, frame types and allowlisted codes', { timeout: 3000 }, async () => {
  const secret = 'secret-token-SQL-header-body';
  const value = await observeDatabaseSeedFailure(failedResponse(JSON.stringify({ type: 'error', code: 'sql_parse_error',
    message: secret, sql: secret, token: secret, headers: { authorization: secret }, unknown: secret })));
  assert.equal(value.httpStatus, 400); assert.equal(value.httpCategory, 'client-error');
  assert.equal(value.bodyState, 'complete'); assert.equal(value.format, 'json'); assert.equal(value.frameCount, 1);
  assert.deepEqual(value.frameTypes, ['error']); assert.deepEqual(value.codes, ['sql_parse_error']);
  assert.equal(value.unknownCodePresent, false); assert.equal(value.rawBodyOrSqlOrHeadersPersisted, false);
  assert.equal(JSON.stringify(value).includes(secret), false);
  assert.deepEqual(Object.keys(value), ['schema', 'httpStatus', 'httpCategory', 'bodyState', 'observedBytes', 'reads',
    'format', 'frameCount', 'frameTypes', 'codes', 'unknownCodePresent', 'rawBodyOrSqlOrHeadersPersisted']);
});

test('unknown or nested failed seed values never become exported code or frame type', { timeout: 3000 }, async () => {
  const secret = 'never-exported-unknown';
  const text = [JSON.stringify({ type: secret, code: secret, error: { code: secret, message: secret } }),
    JSON.stringify({ type: 'error', code: 'sql_execution_error', message: secret })].join('\n');
  const value = await observeDatabaseSeedFailure(failedResponse(text, 500));
  assert.equal(value.httpCategory, 'server-error'); assert.equal(value.format, 'ndjson'); assert.equal(value.frameCount, 2);
  assert.deepEqual(value.frameTypes, ['unknown', 'error']); assert.deepEqual(value.codes, ['sql_execution_error']);
  assert.equal(value.unknownCodePresent, true); assert.equal(JSON.stringify(value).includes(secret), false);
});

test('failed seed reads retain at most 8192 bytes and refuse a large chunk before parsing', { timeout: 3000 }, async () => {
  let cancelled = 0;
  const value = await observeDatabaseSeedFailure({ status: 413, body: new ReadableStream({ start(controller) {
    controller.enqueue(new Uint8Array(100_000).fill(65));
  }, cancel() { cancelled += 1; } }) });
  assert.equal(value.bodyState, 'too-large'); assert.equal(value.observedBytes, 8193); assert.equal(value.reads, 1);
  assert.equal(value.format, null); assert.equal(value.frameCount, null); assert.deepEqual(value.codes, []);
  assert.equal(cancelled, 1);
});

test('failed seed zero-byte chunk flood stops at 16 reads and cancels without polling', { timeout: 3000 }, async () => {
  let reads = 0;
  let cancelled = 0;
  const reader = { read: async () => { reads += 1; return { done: false, value: new Uint8Array() }; },
    cancel: async () => { cancelled += 1; } };
  const value = await observeDatabaseSeedFailure({ status: 400, body: { getReader: () => reader } });
  assert.equal(value.bodyState, 'read-limit'); assert.equal(value.reads, 16); assert.equal(reads, 16);
  assert.equal(value.observedBytes, 0); assert.equal(value.frameCount, null); assert.equal(cancelled, 1);
});

test('incomplete JSON, excessive NDJSON and invalid UTF8 leave parsing unknown without raw errors', { timeout: 3000 }, async () => {
  const expires = Date.now() + 2000;
  for (const response of [failedResponse('{"message":"never-export"'),
    failedResponse(Array.from({ length: 9 }, () => '{"type":"error"}').join('\n')),
    { status: 400, body: new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array([255])); controller.close(); } }) }]) {
    assert.ok(Date.now() < expires);
    const value = await observeDatabaseSeedFailure(response, { deadline: expires });
    assert.equal(value.bodyState, 'invalid-json'); assert.equal(value.frameCount, null); assert.deepEqual(value.codes, []);
    assert.equal(JSON.stringify(value).includes('never-export'), false);
  }
});

test('failed seed cancellation and expired deadlines do not acquire a body reader', { timeout: 3000 }, async () => {
  let acquired = 0;
  const response = { status: 400, body: { getReader() { acquired += 1; throw new Error('secret-body-error'); } } };
  const controller = new AbortController(); controller.abort(new Error('secret-cancel'));
  const cancelled = await observeDatabaseSeedFailure(response, { signal: controller.signal });
  const expired = await observeDatabaseSeedFailure(response, { deadline: Date.now() - 1 });
  assert.equal(cancelled.bodyState, 'cancelled'); assert.equal(expired.bodyState, 'timeout'); assert.equal(acquired, 0);
  assert.equal(JSON.stringify([cancelled, expired]).includes('secret'), false);
});

test('pending failed seed read stops at the supplied deadline and requests cancellation once', { timeout: 3000 }, async () => {
  let cancelled = 0;
  const reader = { read: () => new Promise(() => {}), cancel: async () => { cancelled += 1; } };
  const value = await observeDatabaseSeedFailure({ status: 400, body: { getReader: () => reader } }, { deadline: Date.now() + 25 });
  assert.equal(value.bodyState, 'timeout'); assert.equal(value.reads, 1); assert.equal(cancelled, 1);
});

test('HTTP failure identity survives malformed streams and a throwing evidence recorder', { timeout: 3000 }, async () => {
  const primary = new Error('original HTTP failure');
  let observed;
  await assert.rejects(rejectDatabaseSeedHttpFailure(primary, { status: 400, body: { getReader() {
    throw new Error('secret-observer-error');
  } } }, {}, (value) => { observed = value; throw new Error('secret-record-error'); }), (error) => error === primary);
  assert.equal(observed.bodyState, 'read-error'); assert.equal(JSON.stringify(observed).includes('secret'), false);
});

test('corrected seed uses native STRING and writable ordinary key before any database journey action', { timeout: 3000 }, async () => {
  const calls = [];
  let result;
  let domAccesses = 0;
  const primary = new Error('real seed HTTP 400');
  await assert.rejects(runDatabaseRecoveryScenario({ origin, auth: 'secret-auth', runId: 'test-corrected',
    check: () => {}, setStage: () => {}, setResult: (value) => { result = value; }, launchIdentityKey: () => '100:first',
    getPage: () => { domAccesses += 1; throw new Error('journey should not start'); },
    api: async (method, apiPath, body, auth, raw, observation) => {
      calls.push({ method, apiPath, body, raw, operation: observation?.operation });
      if (calls.length === 1) return '{"type":"end","rowCount":0,"recordsAffected":0,"elapsedMilliseconds":1}';
      return rejectDatabaseSeedHttpFailure(primary, failedResponse('{"type":"error","code":"sql_parse_error","message":"secret-auth"}'), {}, observation.record);
    } }), (error) => error === primary);
  assert.equal(calls.length, 2); assert.equal(calls[1].operation, 'create-table');
  assert.equal(calls[1].body.sql, 'CREATE TABLE "WB61Probe" ("Marker" STRING, PRIMARY KEY ("Marker"))');
  assert.equal(domAccesses, 0); assert.deepEqual(result.selections, []); assert.equal(result.firstClose, null);
  assert.equal(result.secondLaunch, null); assert.equal(result.restored, null); assert.equal(result.query, null);
  assert.equal(result.secondClose, null); assert.equal(result.passed, false); assert.equal(result.seedFailures.length, 1);
  assert.equal(result.seedFailures[0].operation, 'create-table'); assert.equal(JSON.stringify(result).includes('secret-auth'), false);
});

test('selection checkpoint synchronously retains both pre-action barriers without later mutation', { timeout: 3000 }, () => {
  const result = { selectionAttempts: [] };
  const raw = { afterSequence: 5, afterRequestSequence: 43 };
  const attempt = beginDatabaseSelectionAttempt(result, 'A', raw);
  assert.equal(result.selectionAttempts[0], attempt); assert.equal(attempt.phase, 'before-click');
  raw.afterSequence = 6; raw.afterRequestSequence = 44;
  assert.deepEqual(attempt.barrier, { afterSequence: 5, afterRequestSequence: 43 });
  assert.deepEqual(attempt.ackPoll, { callbackCalls: 0, elapsedMs: null });
});

test('selection candidates distinguish a late response to an old request using both original predicates', { timeout: 3000 }, () => {
  const stale = { ...ack(databaseA, 6), requestSequence: 43 };
  const fresh = { ...ack(databaseA, 7), requestSequence: 44 };
  const projected = projectDatabaseSelectionCandidates([stale, fresh], { afterSequence: 5, afterRequestSequence: 43 }, databaseA);
  assert.equal(projected.state, 'complete'); assert.equal(projected.candidateCount, 2);
  assert.deepEqual(projected.candidates.map((candidate) => candidate.ordinal), [0, 1]);
  assert.equal(projected.candidates[0].predicates.receiptAfterBarrier, true);
  assert.equal(projected.candidates[0].predicates.requestAfterBarrier, false);
  assert.deepEqual(projected.candidates[0].rejectedBy, ['requestAfterBarrier']);
  assert.equal(projected.candidates[1].reason, 'find-predicates-match');
  assert.throws(() => assertDatabaseAcknowledgement(stale, { afterSequence: 5, afterRequestSequence: 43, launch: 1, method: 'PUT', database: databaseA, origin }));
});

test('invalid and accessor candidate fields remain unknown and never export raw values', { timeout: 3000 }, () => {
  let getterCalls = 0;
  const secret = 'secret-text-SQL-header-token';
  const candidate = { ...ack(databaseA, 6), sequence: secret, method: secret, path: secret, httpStatus: secret,
    body: { activeDatabase: secret, sql: secret, token: secret, headers: secret } };
  Object.defineProperty(candidate, 'requestSequence', { get() { getterCalls += 1; throw new Error(secret); } });
  const projected = projectDatabaseSelectionCandidates([candidate], { afterSequence: 5, afterRequestSequence: 43 }, databaseA);
  assert.equal(projected.candidates[0].predicates.receiptAfterBarrier, null);
  assert.equal(projected.candidates[0].predicates.requestAfterBarrier, null);
  assert.equal(projected.candidates[0].predicates.status200, null);
  assert.equal(projected.candidates[0].reason, 'predicate-unknown'); assert.equal(getterCalls, 0);
  assert.equal(JSON.stringify(projected).includes(secret), false);
  assert.equal(JSON.stringify(projected).includes('activeDatabase'), false);
});

test('candidate source caps, expired observation and cancellation preserve an unknown boundary', { timeout: 3000 }, () => {
  const projected = projectDatabaseSelectionCandidates(Array.from({ length: 129 }, () => ack(databaseA, 6)), barrier(5), databaseA);
  assert.equal(projected.state, 'count-limit'); assert.equal(projected.candidateCount, null); assert.deepEqual(projected.candidates, []);
  const expired = projectDatabaseSelectionCandidates([ack(databaseA, 6)], barrier(5), databaseA, { deadline: Date.now() - 1 });
  assert.equal(expired.state, 'timeout'); assert.deepEqual(expired.candidates, []);
  const refused = projectDatabaseSelectionCandidates([ack(databaseA, 6)], barrier(5), databaseA, { check: () => { throw new Error('secret-cancel'); } });
  assert.equal(refused.state, 'unknown'); assert.equal(JSON.stringify(refused).includes('secret'), false);
  assert.deepEqual(projectDatabaseSelectionCandidates([], barrier(5), databaseA).candidates, []);
  assert.equal(projectDatabaseSelectionCandidates(null, barrier(5), databaseA).state, 'unknown');
});

test('ordinary DOM projection exports only bounded counts and typed booleans', { timeout: 3000 }, () => {
  const value = { activeNodeCount: 1, identityNodeCount: 1, activeMatchesTarget: true, identityMatchesTarget: true,
    contractWarningPresent: false, activeText: 'secret', identityText: 'secret', token: 'secret' };
  const projected = projectDatabaseSelectionDom(value);
  assert.equal(projected.state, 'observed'); assert.equal(projected.activeMatchesTarget, true);
  assert.equal(JSON.stringify(projected).includes('secret'), false);
  Object.defineProperty(value, 'activeMatchesTarget', { get() { throw new Error('secret'); } });
  assert.equal(projectDatabaseSelectionDom(value).state, 'unknown');
  value.identityNodeCount = 17; value.contractWarningPresent = 'secret';
  const refused = projectDatabaseSelectionDom(value);
  assert.equal(refused.state, 'unknown'); assert.equal(refused.identityNodeCount, null);
  assert.equal(refused.activeMatchesTarget, null); assert.equal(refused.contractWarningPresent, null);
});

test('failed DOM observation and cancellation preserve the original selection error and fixed checkpoint', { timeout: 3000 }, async () => {
  const primary = new Error('original selection failure');
  const result = { selectionAttempts: [] }; const attempt = beginDatabaseSelectionAttempt(result, 'A', barrier(5)); attempt.phase = 'ack-poll';
  await assert.rejects(rejectDatabaseSelectionFailure(primary, attempt, { candidates: [ack(databaseA, 6)], barrier: barrier(5), database: databaseA,
    readDom: async () => { throw new Error('secret DOM failure'); } }), (error) => error === primary);
  assert.equal(attempt.phase, 'failed'); assert.equal(attempt.failure.phase, 'ack-poll'); assert.equal(attempt.failure.terminationReason, 'unknown');
  assert.equal(attempt.failure.dom.state, 'unknown'); assert.equal(JSON.stringify(attempt).includes('secret'), false);
  let reads = 0;
  const cancelled = beginDatabaseSelectionAttempt(result, 'B', barrier(7));
  await assert.rejects(rejectDatabaseSelectionFailure(primary, cancelled, { candidates: [], barrier: barrier(7), database: databaseB,
    check: () => { throw new Error('secret cancellation'); }, readDom: async () => { reads += 1; } }), (error) => error === primary);
  assert.equal(reads, 0); assert.equal(cancelled.failure.dom.state, 'unknown');
  const refusedOptions = { candidates: [], barrier: barrier(7), database: databaseB };
  Object.defineProperty(refusedOptions, 'readDom', { get() { throw new Error('secret observer getter'); } });
  await assert.rejects(rejectDatabaseSelectionFailure(primary, cancelled, refusedOptions), (error) => error === primary);
  assert.equal(cancelled.failure.dom.state, 'unknown'); assert.equal(JSON.stringify(cancelled).includes('secret'), false);
});

test('failed DOM observation timeout remains unknown and never replaces the original error', { timeout: 3000 }, async () => {
  const primary = new Error('original click failure');
  const attempt = beginDatabaseSelectionAttempt({ selectionAttempts: [] }, 'A', barrier(5)); attempt.phase = 'click';
  await assert.rejects(rejectDatabaseSelectionFailure(primary, attempt, { candidates: [], barrier: barrier(5), database: databaseA,
    readDom: () => new Promise(() => {}) }), (error) => error === primary);
  assert.equal(attempt.failure.dom.state, 'unknown'); assert.equal(attempt.failure.phase, 'click');
});

const selectionHarness = (mode, primary) => {
  let result; let selected = 'WB61_Initial_Test'; let clicks = 0; let apiCalls = 0; let observerReads = 0;
  const candidates = []; const pollOptions = [];
  const button = { filter: () => button, count: async () => 1, locator: () => button, click: async () => {
    const attempt = result.selectionAttempts[clicks];
    assert.equal(attempt.phase, 'click'); assert.deepEqual(attempt.barrier, barrier(clicks === 0 ? 5 : 7));
    clicks += 1; selected = clicks === 1 ? result.databaseA : result.databaseB;
    if (mode === 'click') throw primary;
    candidates.push(ack(selected, clicks === 1 ? 6 : 8));
  } };
  const page = { getByTitle: () => ({ count: async () => 0, click: async () => {} }),
    locator: (selector) => selector === '.schema-group--databases' ? { locator: () => ({ count: async () => 1 }) } : button,
    evaluate: async (_, args) => {
      if (args && Object.hasOwn(args, 'identityPrefix')) {
        observerReads += 1;
        const rendered = dom(selected);
        return { activeNodeCount: 1, identityNodeCount: 1, activeMatchesTarget: rendered.activeDatabase === args.database,
          identityMatchesTarget: rendered.hostIdentity === `${args.identityPrefix}${args.database}`,
          activeIdentityConsistent: rendered.hostIdentity === `${args.identityPrefix}${rendered.activeDatabase}`, contractWarningPresent: false };
      }
      if (args) { observerReads += 1; return { activeNodeCount: 1, identityNodeCount: 1, activeMatchesTarget: true, identityMatchesTarget: true, contractWarningPresent: false }; }
      return dom(selected);
    } };
  const harness = { origin, runId: 'selection-test', auth: 'secret-auth', check: () => {}, evidence: async () => {},
    bridgeEvidence: candidates, getPage: () => page, launchIdentityKey: () => '100:first', setStage: () => {}, setResult: (value) => { result = value; },
    observationBarrier: () => barrier(clicks === 0 ? 5 : 7), readLibrary: async () => ({ snapshot: disk(selected) }),
    closeDesktop: async () => { throw primary; },
    api: async (_, apiPath, body) => {
      apiCalls += 1; assert.ok(apiCalls <= 8);
      if (!body.sql.startsWith('SELECT')) return '{"type":"end","rowCount":0,"recordsAffected":0,"elapsedMilliseconds":1}';
      return [JSON.stringify({ type: 'meta', columns: ['Marker'] }), JSON.stringify([apiPath.includes('Bravo') ? 'WB61_B' : 'WB61_A']),
        JSON.stringify({ type: 'end', rowCount: 1, recordsAffected: -1, elapsedMilliseconds: 1 })].join('\n');
    },
    poll: async (label, callback, options) => {
      pollOptions.push({ label, options });
      if (label === 'Both real seeded databases in ordinary Explorer') return true;
      if (mode === 'poll' && label === 'Fresh ordinary selection native PUT') { await callback(); throw primary; }
      return callback();
    } };
  return { harness, result: () => result, counts: () => ({ clicks, apiCalls, observerReads }), pollOptions };
};

test('real scenario click and poll failure paths keep the exact original error and pre-click checkpoint', { timeout: 3000 }, async () => {
  const expires = Date.now() + 2000;
  for (const mode of ['click', 'poll']) {
    assert.ok(Date.now() < expires);
    const primary = new Error(`original ${mode}`); const fixture = selectionHarness(mode, primary);
    await assert.rejects(runDatabaseRecoveryScenario(fixture.harness), (error) => error === primary);
    const result = fixture.result(); const attempt = result.selectionAttempts[0];
    assert.equal(result.passed, false); assert.equal(result.firstClose, null); assert.deepEqual(result.selections, []);
    assert.equal(attempt.failure.phase, mode === 'click' ? 'click' : 'ack-poll');
    assert.equal(attempt.ackPoll.callbackCalls, mode === 'click' ? 0 : 1);
    assert.equal(mode === 'click' ? attempt.ackPoll.elapsedMs === null : Number.isSafeInteger(attempt.ackPoll.elapsedMs), true);
    assert.equal(fixture.counts().apiCalls, 8); assert.equal(fixture.counts().observerReads, 2);
    assert.equal(JSON.stringify(result).includes('original'), false); assert.equal(JSON.stringify(result).includes('secret-auth'), false);
  }
});

test('successful A and B selections retain original poll options and acceptance with one precondition read per selection', { timeout: 3000 }, async () => {
  const stop = new Error('outside selection normal-close test boundary'); const fixture = selectionHarness('success', stop);
  await assert.rejects(runDatabaseRecoveryScenario(fixture.harness), (error) => error === stop);
  const result = fixture.result(); assert.equal(result.selections.length, 2); assert.equal(result.passed, false);
  assert.deepEqual(result.selectionAttempts.map((attempt) => attempt.phase), ['accepted', 'accepted']);
  assert.deepEqual(result.selectionAttempts.map((attempt) => attempt.failure), [null, null]);
  assert.equal(fixture.counts().observerReads, 2); assert.equal(fixture.counts().clicks, 2);
  const ackPolls = fixture.pollOptions.filter((item) => item.label === 'Fresh ordinary selection native PUT');
  assert.equal(ackPolls.length, 2); assert.deepEqual(ackPolls.map((item) => item.options),
    [{ attempts: 30, timeoutMs: 20_000, intervalMs: 250 }, { attempts: 30, timeoutMs: 20_000, intervalMs: 250 }]);
});
