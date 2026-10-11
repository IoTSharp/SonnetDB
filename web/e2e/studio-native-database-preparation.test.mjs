import assert from 'node:assert/strict';
import test from 'node:test';
import { projectDatabaseSnapshot, runDatabaseRecoveryScenario } from './studio-native-database-scenario.mjs';

const origin = 'http://127.0.0.1:18338';
const identityPrefix = `studio-desktop · managed-local · ${origin} · `;
const validClose = () => ({ method: 'CloseMainWindow', accepted: true, exitCode: 0, exitSignal: null,
  studioIdentityExited: true, serverIdentityExited: true, allOwnedIdentitiesExited: true,
  allFourPortsReleased: true, fallbackUsed: false });
const snapshot = (database, disk = false) => {
  const identity = { host: 'studio-desktop', profileId: 'managed-local', baseUrl: origin, database };
  return projectDatabaseSnapshot({ activeProfileId: 'managed-local', activeDatabase: database,
    profiles: [{ id: 'managed-local', kind: 'managed-local', baseUrl: origin, defaultDatabase: database,
      tokenMode: 'current-session', ...(disk ? {} : { identity }) }], ...(disk ? {} : { activeIdentity: identity }) }, { disk });
};
const sqlResult = (sentinel) => [JSON.stringify({ type: 'meta', columns: ['Marker'] }), JSON.stringify([sentinel]),
  JSON.stringify({ type: 'end', rowCount: 1, recordsAffected: -1, elapsedMilliseconds: 1 })].join('\n');

// The browser fixture runs the actual production evaluate callbacks against
// rendered nodes. Each exact rendered button changes the selected database and
// independently produces its request/receipt. No callback supplies authority,
// changes the contract phase, or adds a journey selection.
const fixture = (options = {}) => {
  let result;
  let active;
  let identityDatabase;
  let activeCount = 1;
  let identityCount = 1;
  let warning = false;
  let launch = 1;
  let sequence = 10;
  let requestSequence = 20;
  let reads = 0;
  let observerReads = 0;
  let apiCalls = 0;
  let diskReads = 0;
  let closeCalls = 0;
  let relaunches = 0;
  let typedSql;
  let guardFailure;
  const candidates = [];
  const events = [];
  const clicks = [];
  const polls = [];
  const writes = [];
  const barriers = [];
  const label = (database) => database === result.databaseA ? 'A' : database === result.databaseB ? 'B' : 'other';
  const state = () => ({ targetA: result.databaseA, targetB: result.databaseB, active,
    setActive: (value) => { active = value; }, setIdentity: (value) => { identityDatabase = value; },
    setCounts: (a, b) => { activeCount = a; identityCount = b; }, setWarning: (value) => { warning = value; },
    refuse: (error) => { guardFailure = error; } });
  const check = () => { if (guardFailure) throw guardFailure; };
  const evaluate = async (callback, args) => {
    if (args && Object.hasOwn(args, 'identityPrefix')) {
      reads += 1; assert.ok(reads <= 4);
      events.push(`read:${label(args.database)}`);
      options.beforeRead?.({ ...state(), target: args.database, index: reads });
    } else if (args) observerReads += 1;
    const previousDocument = Object.getOwnPropertyDescriptor(globalThis, 'document');
    const nodes = Array.from({ length: activeCount }, () => ({ querySelector: () => ({ textContent: active }) }));
    const identities = Array.from({ length: identityCount }, () => ({ textContent: `${identityPrefix}${identityDatabase ?? active}` }));
    Object.defineProperty(globalThis, 'document', { configurable: true, value: {
      querySelectorAll: (selector) => selector === '[data-testid="studio-host-identity"]' ? identities : nodes,
      querySelector: (selector) => selector === '[data-testid="studio-host-identity"]' ? identities[0] ?? null : warning ? {} : null,
    } });
    try { return callback(args); }
    finally {
      if (previousDocument) Object.defineProperty(globalThis, 'document', previousDocument);
      else delete globalThis.document;
    }
  };
  const button = (database) => {
    const element = {
      count: async () => options.nonUniquePreparation && clicks.length === 0 && database === result.databaseB ? 2 : 1,
      locator: () => element,
      click: async (clickOptions) => {
        assert.deepEqual(clickOptions, { timeout: 5000 });
        const preparing = result.firstSelectionPreparation?.attempt?.phase === 'click';
        if (preparing) {
          assert.equal(database, result.databaseB);
          assert.equal(result.selectionAttempts.length, 0); assert.equal(result.selections.length, 0);
        }
        events.push(`click:${label(database)}`); clicks.push({ database, preparing });
        assert.ok(clicks.length <= 3);
        if (preparing && options.clickFailure) throw options.clickFailure;
        if (!(preparing && options.noEffectPreparation)) active = database;
        identityDatabase = undefined;
        requestSequence += 1; sequence += 1;
        const acknowledgement = { sequence, requestSequence, launch, method: 'PUT', path: '/studio-bridge/connections',
          httpStatus: 200, body: snapshot(database) };
        if (preparing) options.mutatePreparationAck?.(acknowledgement, state(), barriers.at(-1));
        candidates.push(acknowledgement);
      },
    };
    return element;
  };
  const region = { getByRole: () => ({ click: async () => {} }), locator: () => ({ allTextContents: async () => ['WB61_B'] }) };
  const page = {
    evaluate,
    getByTitle: () => ({ count: async () => 0, click: async () => {} }),
    locator: (selector, locatorOptions) => {
      if (selector === '.schema-group--databases') return { locator: () => ({ count: async () => 1 }) };
      if (selector === '.sql-editor .cm-content') return { click: async () => {} };
      if (selector === '.schema-database-node .schema-item--database strong') return { allTextContents: async () => [result.databaseA, result.databaseB] };
      if (selector === 'strong') return { matcher: locatorOptions.hasText };
      assert.equal(selector, '.schema-database-node .schema-item--database');
      return { filter: ({ has }) => {
        const database = has.matcher.test(result.databaseA) ? result.databaseA : has.matcher.test(result.databaseB) ? result.databaseB : undefined;
        assert.ok(database); return button(database);
      } };
    },
    keyboard: { press: async () => {}, insertText: async (value) => { typedSql = value; } },
    getByRole: () => ({ click: async () => {} }), getByTestId: () => region,
    waitForResponse: async (predicate, responseOptions) => {
      assert.deepEqual(responseOptions, { timeout: 20_000 });
      const response = { url: () => `${origin}/v1/db/${encodeURIComponent(result.databaseB)}/sql`, status: () => 200,
        request: () => ({ method: () => 'POST', postDataJSON: () => ({ sql: typedSql }) }), text: async () => sqlResult('WB61_B') };
      assert.equal(predicate(response), true); return response;
    },
  };
  const harness = { origin, runId: 'wb68-preparation', auth: 'SECRET_AUTH', check,
    ...(Object.hasOwn(options, 'optIn') ? { prepareFirstSelection: options.optIn } : { prepareFirstSelection: true }),
    setStage: () => {}, setResult: (value) => {
      result = value; active = options.initial === 'other' ? 'WB68_Initial' : value.databaseA;
    },
    launchIdentityKey: () => launch === 1 ? '100:first' : '101:second', getPage: () => page, bridgeEvidence: candidates,
    observationBarrier: () => {
      events.push('barrier'); const value = { afterSequence: sequence, afterRequestSequence: requestSequence };
      barriers.push({ ...value }); return value;
    },
    evidence: async (name, value) => { writes.push({ name, value }); },
    api: async (_, apiPath, body) => {
      apiCalls += 1; assert.ok(apiCalls <= 8);
      if (!body.sql.startsWith('SELECT')) return '{"type":"end","rowCount":0,"recordsAffected":0,"elapsedMilliseconds":1}';
      return sqlResult(apiPath.includes('Bravo') ? 'WB61_B' : 'WB61_A');
    },
    poll: async (pollLabel, callback, pollOptions) => {
      check(); polls.push({ label: pollLabel, options: pollOptions }); assert.ok(polls.length <= 11);
      if (pollLabel === 'Fresh ordinary preparation native PUT' && options.pollFailure) { await callback(); throw options.pollFailure; }
      const value = await callback();
      if (!value) throw options.rejectedPoll ?? new Error('synthetic bounded poll rejected');
      return value;
    },
    readLibrary: async () => {
      check(); diskReads += 1; assert.ok(diskReads <= 4);
      if (diskReads === 1 && result.firstSelectionPreparation?.attempt && options.diskFailure) throw options.diskFailure;
      const value = { snapshot: snapshot(active, true) };
      if (diskReads === 1 && result.firstSelectionPreparation?.attempt) options.mutatePreparationDisk?.(value, state());
      return value;
    },
    closeDesktop: async () => {
      closeCalls += 1; assert.ok(closeCalls <= 2);
      return { ...validClose(), ...(options.invalidClose === true || options.invalidClose === closeCalls ? { allOwnedIdentitiesExited: false } : {}) };
    },
    relaunchDesktop: async () => {
      relaunches += 1; assert.equal(relaunches, 1); launch = 2;
      const barrier = { afterSequence: sequence, afterRequestSequence: requestSequence };
      sequence += 1; requestSequence += 1;
      candidates.push({ sequence, requestSequence, launch, method: 'GET', path: '/studio-bridge/connections',
        httpStatus: 200, body: snapshot(active) });
      return { barrier };
    },
  };
  if (options.omitOptIn) delete harness.prepareFirstSelection;
  return { harness, result: () => result, state, events, polls, writes, barriers, clicks, candidates,
    counts: () => ({ reads, observerReads, apiCalls, diskReads, closeCalls, relaunches }) };
};

const assertNoJourney = (value) => {
  const result = value.result();
  assert.equal(result.passed, false); assert.deepEqual(result.selections, []);
  assert.deepEqual([result.firstClose, result.secondLaunch, result.restored, result.query, result.secondClose], [null, null, null, null, null]);
  assert.equal(value.counts().apiCalls, 8); assert.equal(value.counts().closeCalls, 0); assert.equal(value.counts().relaunches, 0);
  assert.equal(value.writes.length, 0);
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
};

test('explicit native preparation performs B once before the unchanged two-selection A to B recovery journey', { timeout: 4000 }, async () => {
  const value = fixture();
  const result = await runDatabaseRecoveryScenario(value.harness);
  assert.equal(result.passed, true);
  assert.equal(result.firstSelectionPreparation.state, 'prepared');
  const prepared = result.firstSelectionPreparation.attempt;
  assert.equal(prepared.phase, 'accepted'); assert.equal(prepared.precondition.state, 'change-required');
  assert.equal(prepared.selection.database, result.databaseB);
  assert.deepEqual(value.clicks.map((item) => [item.database, item.preparing]), [[result.databaseB, true], [result.databaseA, false], [result.databaseB, false]]);
  assert.deepEqual(result.selections.map((item) => item.database), [result.databaseA, result.databaseB]);
  assert.equal(result.selectionAttempts.length, 2);
  assert.deepEqual(result.selectionAttempts.map((item) => item.precondition.state), ['change-required', 'change-required']);
  assert.deepEqual(value.events, ['read:A', 'read:B', 'barrier', 'click:B', 'read:A', 'barrier', 'click:A', 'read:B', 'barrier', 'click:B']);
  assert.deepEqual(value.barriers, [{ afterSequence: 10, afterRequestSequence: 20 }, { afterSequence: 11, afterRequestSequence: 21 }, { afterSequence: 12, afterRequestSequence: 22 }]);
  assert.equal(result.selections[0].acknowledgement.sequence, 12);
  assert.equal(result.selections[1].acknowledgement.sequence, 13);
  assert.equal(result.restored.acknowledgement.launch, 2); assert.equal(result.query.rendered, 'WB61_B');
  assert.deepEqual(value.counts(), { reads: 4, observerReads: 0, apiCalls: 8, diskReads: 4, closeCalls: 2, relaunches: 1 });
  assert.deepEqual(value.writes.map((item) => item.name), ['database-selection-1.json', 'database-selection-2.json',
    'database-first-close.json', 'database-restored.json', 'database-query.json', 'database-second-close.json']);
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
  const ackOptions = value.polls.filter((item) => item.label.includes('native PUT')).map((item) => item.options);
  assert.deepEqual(ackOptions, Array.from({ length: 3 }, () => ({ attempts: 30, timeoutMs: 20_000, intervalMs: 250 })));
});

test('a first A change-required observation consumes no preparation action or extra read', { timeout: 4000 }, async () => {
  const value = fixture({ initial: 'other' });
  const result = await runDatabaseRecoveryScenario(value.harness);
  assert.equal(result.passed, true); assert.equal(result.firstSelectionPreparation.state, 'not-needed');
  assert.equal(result.firstSelectionPreparation.attempt, null);
  assert.deepEqual(value.clicks.map((item) => item.preparing), [false, false]);
  assert.equal(value.counts().reads, 2); assert.equal(value.counts().diskReads, 3);
  assert.deepEqual(value.events, ['read:A', 'barrier', 'click:A', 'read:B', 'barrier', 'click:B']);
});

test('missing, false and nonboolean opt-in preserve legacy same-A refusal and the original result shape', { timeout: 4000 }, async () => {
  const expires = Date.now() + 2000;
  const cases = [{ omitOptIn: true }, { optIn: false }, { optIn: 'true' }];
  for (let index = 0; index < cases.length && index < 3 && Date.now() < expires; index += 1) {
    const value = fixture(cases[index]);
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), /selection precondition refused/u);
    assertNoJourney(value); assert.equal(Object.hasOwn(value.result(), 'firstSelectionPreparation'), false);
    assert.equal(value.clicks.length, 0); assert.equal(value.counts().reads, 1);
    assert.equal(value.result().selectionAttempts.length, 1);
    assert.equal(value.result().selectionAttempts[0].precondition.state, 'target-already-active');
    assert.equal(value.polls.some((item) => item.label.includes('native PUT')), false);
  }
  assert.ok(Date.now() < expires);
});

test('unknown initial A identity, warning, duplicate node and case variant permit no preparation or journey action', { timeout: 4000 }, async () => {
  const changes = [(state) => state.setIdentity('Different'), (state) => state.setWarning(true),
    (state) => state.setCounts(2, 1), (state) => state.setActive(state.targetA.toLowerCase())];
  const expires = Date.now() + 2000;
  for (let index = 0; index < changes.length && index < 4 && Date.now() < expires; index += 1) {
    const value = fixture({ beforeRead: (state) => { if (state.index === 1) changes[index](state); } });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), /selection precondition refused/u);
    assertNoJourney(value); assert.equal(value.clicks.length, 0);
    assert.equal(value.result().firstSelectionPreparation.state, 'refused');
    assert.equal(value.result().firstSelectionPreparation.attempt, null);
    assert.equal(value.result().selectionAttempts[0].precondition.state, 'unknown');
    assert.equal(value.polls.some((item) => item.label.includes('native PUT')), false);
  }
  assert.ok(Date.now() < expires);
});

test('preparatory B same or unknown state is rejected before any preparatory click or acknowledgement poll', { timeout: 4000 }, async () => {
  const cases = [(state) => state.setActive(state.targetB), (state) => state.setCounts(1, 2)];
  const expires = Date.now() + 2000;
  for (let index = 0; index < cases.length && index < 2 && Date.now() < expires; index += 1) {
    const value = fixture({ beforeRead: (state) => { if (state.index === 2) cases[index](state); } });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), /preparation precondition refused/u);
    assertNoJourney(value); assert.equal(value.clicks.length, 0);
    assert.equal(value.result().selectionAttempts.length, 0);
    assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, 'before-click-precondition');
    assert.equal(value.polls.some((item) => item.label.includes('native PUT')), false);
  }
  assert.ok(Date.now() < expires);
});

test('a nonunique ordinary B button is refused without preparation or journey authority', { timeout: 3000 }, async () => {
  const value = fixture({ nonUniquePreparation: true });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /preparation database button is not unique/u);
  assertNoJourney(value); assert.equal(value.clicks.length, 0); assert.equal(value.result().selectionAttempts.length, 0);
  assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, 'button');
});

test('all seven original fresh acknowledgement predicates independently refuse preparation', { timeout: 4000 }, async () => {
  const cases = [
    ['receiptAfterBarrier', (ack, _, barrier) => { ack.sequence = barrier.afterSequence; }],
    ['requestAfterBarrier', (ack, _, barrier) => { ack.requestSequence = barrier.afterRequestSequence; }],
    ['firstLaunch', (ack) => { ack.launch = 2; }], ['putMethod', (ack) => { ack.method = 'GET'; }],
    ['connectionsPath', (ack) => { ack.path = '/unexpected'; }], ['status200', (ack) => { ack.httpStatus = 500; }],
    ['targetDatabase', (ack, state) => { ack.body = snapshot(state.targetA); }],
  ];
  const expires = Date.now() + 2000;
  for (let index = 0; index < cases.length && index < 7 && Date.now() < expires; index += 1) {
    const primary = new Error('SECRET_ORIGINAL_POLL');
    const value = fixture({ mutatePreparationAck: cases[index][1], rejectedPoll: primary });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), (error) => error === primary);
    assertNoJourney(value); assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
    const attempt = value.result().firstSelectionPreparation.attempt;
    assert.equal(attempt.failure.phase, 'ack-poll'); assert.equal(attempt.ackPoll.callbackCalls, 1);
    assert.deepEqual(attempt.failure.candidates.candidates[0].rejectedBy, [cases[index][0]]);
    assert.equal(attempt.selection, null);
  }
  assert.ok(Date.now() < expires);
});

test('fresh B receipt with a wrong default or native identity still refuses preparation', { timeout: 4000 }, async () => {
  const changes = [(ack, state) => { ack.body.profiles[0].defaultDatabase = state.targetA; },
    (ack) => { ack.body.activeIdentity.host = 'web'; }, (ack) => { ack.body.profiles[0].identity.database = 'Different'; }];
  const expires = Date.now() + 2000;
  for (let index = 0; index < changes.length && index < 3 && Date.now() < expires; index += 1) {
    const value = fixture({ mutatePreparationAck: changes[index] });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness));
    assertNoJourney(value); assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
    const attempt = value.result().firstSelectionPreparation.attempt;
    assert.equal(attempt.failure.phase, 'contract'); assert.equal(attempt.selection, null);
    assert.equal(attempt.failure.candidates.candidates[0].reason, 'find-predicates-match');
  }
  assert.ok(Date.now() < expires);
});

test('actual preparation disk default and exact database semantics are required', { timeout: 4000 }, async () => {
  const changes = [(value, state) => { value.snapshot.profiles[0].defaultDatabase = state.targetA; },
    (value, state) => { value.snapshot.activeDatabase = state.targetB.toLowerCase(); }];
  const expires = Date.now() + 2000;
  for (let index = 0; index < changes.length && index < 2 && Date.now() < expires; index += 1) {
    const value = fixture({ mutatePreparationDisk: changes[index] });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness));
    assertNoJourney(value); assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, 'contract');
    assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
  }
  assert.ok(Date.now() < expires);
});

test('an acknowledgement without an ordinary B DOM effect cannot begin the original journey', { timeout: 3000 }, async () => {
  const primary = new Error('SECRET_NO_DOM_EFFECT');
  const value = fixture({ noEffectPreparation: true, rejectedPoll: primary });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), (error) => error === primary);
  assertNoJourney(value); assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
  assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, 'dom-poll');
});

test('a preparation DOM warning refuses complete-looking B acknowledgement and disk', { timeout: 3000 }, async () => {
  const value = fixture({ mutatePreparationAck: (_, state) => state.setWarning(true) });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /preparation rendered/u);
  assertNoJourney(value); assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, 'contract');
  assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
});

test('post-preparation A is re-read and same or unknown state still refuses without a retry', { timeout: 4000 }, async () => {
  const changes = [(state) => state.setActive(state.targetA), (state) => state.setIdentity('Different')];
  const expires = Date.now() + 2000;
  for (let index = 0; index < changes.length && index < 2 && Date.now() < expires; index += 1) {
    const value = fixture({ beforeRead: (state) => { if (state.index === 3) changes[index](state); } });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), /selection precondition refused/u);
    assertNoJourney(value); assert.equal(value.clicks.length, 1);
    assert.equal(value.result().firstSelectionPreparation.state, 'prepared');
    assert.equal(value.result().selectionAttempts.length, 1);
    assert.equal(value.result().selectionAttempts[0].phase, 'failed');
    assert.equal(value.result().selectionAttempts[0].ackPoll.callbackCalls, 0);
    assert.equal(value.polls.filter((item) => item.label === 'Fresh ordinary preparation native PUT').length, 1);
    assert.equal(value.polls.some((item) => item.label === 'Fresh ordinary selection native PUT'), false);
  }
  assert.ok(Date.now() < expires);
});

test('preparatory click, poll and disk failures retain the exact primary error and only one attempted action', { timeout: 4000 }, async () => {
  const cases = [['clickFailure', 'click'], ['pollFailure', 'ack-poll'], ['diskFailure', 'disk']];
  const expires = Date.now() + 2000;
  for (let index = 0; index < cases.length && index < 3 && Date.now() < expires; index += 1) {
    const primary = new Error('SECRET_PRIMARY');
    const value = fixture({ [cases[index][0]]: primary });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), (error) => error === primary);
    assertNoJourney(value); assert.equal(value.clicks.length, 1); assert.equal(value.result().selectionAttempts.length, 0);
    assert.equal(value.result().firstSelectionPreparation.attempt.failure.phase, cases[index][1]);
    assert.equal(value.result().firstSelectionPreparation.attempt.selection, null);
  }
  assert.ok(Date.now() < expires);
});

test('cancellation and wall-clock guard refusal before preparatory click preserve the guard error without further actions', { timeout: 4000 }, async () => {
  const cases = [1, 2];
  const expires = Date.now() + 2000;
  for (let index = 0; index < cases.length && index < 2 && Date.now() < expires; index += 1) {
    const primary = new Error(index === 0 ? 'SECRET_CANCELLED' : 'SECRET_DEADLINE');
    const value = fixture({ beforeRead: (state) => { if (state.index === cases[index]) state.refuse(primary); } });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), (error) => error === primary);
    assertNoJourney(value); assert.equal(value.clicks.length, 0);
    assert.equal(value.polls.some((item) => item.label.includes('native PUT')), false);
  }
  assert.ok(Date.now() < expires);
});

test('a pending preparatory B read respects its two-second wait and handles a late rejection', { timeout: 4000 }, async () => {
  let rejectRead;
  const ready = fixture();
  const originalEvaluate = ready.harness.getPage().evaluate;
  let prechecks = 0;
  ready.harness.getPage().evaluate = async (callback, args) => {
    if (args && Object.hasOwn(args, 'identityPrefix')) {
      prechecks += 1;
      if (prechecks === 2) return new Promise((_, reject) => { rejectRead = reject; });
    }
    return originalEvaluate(callback, args);
  };
  const started = Date.now();
  await assert.rejects(runDatabaseRecoveryScenario(ready.harness), /preparation precondition refused/u);
  assert.ok(Date.now() - started >= 1900 && Date.now() - started < 3500);
  assertNoJourney(ready); assert.equal(ready.clicks.length, 0);
  assert.equal(ready.result().firstSelectionPreparation.attempt.precondition.reason, 'timeout');
  rejectRead(new Error('SECRET_LATE_REJECTION')); await Promise.resolve();
});

test('ordinary preparation cannot replace the original first strict normal-close contract', { timeout: 3000 }, async () => {
  const value = fixture({ invalidClose: true });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /strict absence/u);
  assert.equal(value.result().firstSelectionPreparation.state, 'prepared');
  assert.equal(value.result().selections.length, 2); assert.equal(value.result().passed, false);
  assert.equal(value.result().secondLaunch, null); assert.equal(value.counts().relaunches, 0);
});

test('ordinary preparation cannot replace the original second strict normal-close contract', { timeout: 3000 }, async () => {
  const value = fixture({ invalidClose: 2 });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /strict absence/u);
  assert.equal(value.result().firstSelectionPreparation.state, 'prepared');
  assert.equal(value.result().selections.length, 2); assert.equal(value.result().passed, false);
  assert.equal(value.result().restored.acknowledgement.launch, 2); assert.equal(value.result().query.rendered, 'WB61_B');
  assert.equal(value.result().secondClose.allOwnedIdentitiesExited, false);
  assert.equal(value.counts().closeCalls, 2); assert.equal(value.counts().relaunches, 1);
});
