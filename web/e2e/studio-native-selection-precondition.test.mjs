import assert from 'node:assert/strict';
import test from 'node:test';
import { observeDatabaseSelectionPrecondition, projectDatabaseSelectionPrecondition, projectDatabaseSnapshot,
  runDatabaseRecoveryScenario } from './studio-native-database-scenario.mjs';

const origin = 'http://127.0.0.1:18338';
const prefix = `studio-desktop · managed-local · ${origin} · `;
const close = () => ({ method: 'CloseMainWindow', accepted: true, exitCode: 0, exitSignal: null,
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
const validObservation = (matches = false) => ({ activeNodeCount: 1, identityNodeCount: 1,
  activeMatchesTarget: matches, identityMatchesTarget: matches, activeIdentityConsistent: true, contractWarningPresent: false });

// The fixture executes the actual page.evaluate callback against ordinary DOM
// nodes. It supplies no selection authority or substitute acknowledgement.
const fixture = ({ initial = 'other', beforeRead, readFailure, cancelledCheck, inspectPredicates = false, invalidClose = false } = {}) => {
  let result;
  let active = 'WB65_Initial';
  let identityDatabase;
  let activeCount = 1;
  let identityCount = 1;
  let warning = false;
  let launch = 1;
  let clicks = 0;
  let preconditionReads = 0;
  let observerReads = 0;
  let apiCalls = 0;
  let closeCalls = 0;
  let relaunches = 0;
  let typedSql;
  let guardFailure;
  const candidates = [];
  const events = [];
  const polls = [];
  const writes = [];
  const barriers = [];
  const counts = () => ({ clicks, preconditionReads, observerReads, apiCalls, closeCalls, relaunches });
  const identityText = () => `${prefix}${identityDatabase ?? active}`;
  const evaluate = async (callback, args) => {
    const precheck = args && Object.hasOwn(args, 'identityPrefix');
    if (precheck) {
      preconditionReads += 1;
      assert.ok(preconditionReads <= 2);
      events.push(`precheck-${preconditionReads}`);
      beforeRead?.({ target: args.database, active, setActive: (value) => { active = value; },
        setIdentity: (value) => { identityDatabase = value; }, setCounts: (a, b) => { activeCount = a; identityCount = b; },
        setWarning: (value) => { warning = value; } });
      if (cancelledCheck) guardFailure = cancelledCheck;
      if (readFailure) return readFailure();
    } else if (args) observerReads += 1;
    const previousDocument = Object.getOwnPropertyDescriptor(globalThis, 'document');
    const nodes = Array.from({ length: activeCount }, () => ({ querySelector: () => ({ textContent: active }) }));
    const identities = Array.from({ length: identityCount }, () => ({ textContent: identityText() }));
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
  const button = { filter: () => button, count: async () => 1, locator: () => button, click: async () => {
    const attempt = result.selectionAttempts[clicks];
    assert.equal(attempt.phase, 'click'); assert.equal(attempt.precondition.state, 'change-required');
    assert.equal(attempt.precondition.readCalls, 1);
    assert.deepEqual(attempt.barrier, barriers[clicks]);
    events.push(`click-${clicks + 1}`);
    clicks += 1; active = clicks === 1 ? result.databaseA : result.databaseB; identityDatabase = undefined;
    const good = { sequence: clicks === 1 ? 11 : 13, requestSequence: clicks === 1 ? 21 : 23,
      launch: 1, method: 'PUT', path: '/studio-bridge/connections', httpStatus: 200, body: snapshot(active) };
    if (inspectPredicates) candidates.push(
      { ...good, sequence: barriers[clicks - 1].afterSequence },
      { ...good, requestSequence: barriers[clicks - 1].afterRequestSequence },
      { ...good, launch: 2 }, { ...good, method: 'GET' }, { ...good, path: '/unexpected' },
      { ...good, httpStatus: 500 }, { ...good, body: snapshot('WrongCase') });
    candidates.push(good);
  } };
  const region = { getByRole: () => ({ click: async () => {} }), locator: () => ({ allTextContents: async () => ['WB61_B'] }) };
  const page = { evaluate, getByTitle: () => ({ count: async () => 0, click: async () => {} }),
    locator: (selector) => selector === '.schema-group--databases' ? { locator: () => ({ count: async () => 1 }) }
      : selector === '.sql-editor .cm-content' ? { click: async () => {} } : button,
    keyboard: { press: async () => {}, insertText: async (value) => { typedSql = value; } },
    getByRole: () => ({ click: async () => {} }), getByTestId: () => region,
    waitForResponse: async (predicate, options) => {
      assert.deepEqual(options, { timeout: 20_000 });
      const response = { url: () => `${origin}/v1/db/${encodeURIComponent(result.databaseB)}/sql`, status: () => 200,
        request: () => ({ method: () => 'POST', postDataJSON: () => ({ sql: typedSql }) }), text: async () => sqlResult('WB61_B') };
      assert.equal(predicate(response), true); return response;
    } };
  const harness = { origin, runId: 'wb65-precondition', auth: 'SECRET_AUTH', check: () => { if (guardFailure) throw guardFailure; },
    setStage: () => {}, setResult: (value) => {
      result = value;
      if (initial === 'same') active = value.databaseA;
      if (initial === 'case') active = value.databaseA.toLowerCase();
    }, launchIdentityKey: () => launch === 1 ? '100:first' : '101:second', getPage: () => page, bridgeEvidence: candidates,
    observationBarrier: () => {
      assert.equal(preconditionReads, clicks + 1);
      assert.equal(result.selectionAttempts.length, clicks);
      events.push(`barrier-${clicks + 1}`);
      const barrier = { afterSequence: clicks === 0 ? 10 : 12, afterRequestSequence: clicks === 0 ? 20 : 22 };
      barriers.push({ ...barrier }); return barrier;
    },
    evidence: async (name, value) => { writes.push({ name, value }); },
    api: async (_, apiPath, body) => {
      apiCalls += 1; assert.ok(apiCalls <= 8);
      if (!body.sql.startsWith('SELECT')) return '{"type":"end","rowCount":0,"recordsAffected":0,"elapsedMilliseconds":1}';
      return sqlResult(apiPath.includes('Bravo') ? 'WB61_B' : 'WB61_A');
    },
    poll: async (label, callback, options) => {
      polls.push({ label, options }); assert.ok(polls.length <= 10);
      if (label === 'Both real seeded databases in ordinary Explorer') return true;
      if (inspectPredicates && label === 'Fresh ordinary selection native PUT') {
        const retained = candidates.slice();
        const latest = retained.slice(-8);
        const expires = Date.now() + 1000;
        // Seven named predicate failures, one pass: fixed eight items, one second.
        for (let index = 0; index < 7 && Date.now() < expires; index += 1) {
          candidates.splice(0, candidates.length, latest[index]);
          assert.equal(await callback(), undefined);
        }
        assert.ok(Date.now() < expires);
        candidates.splice(0, candidates.length, ...retained);
      }
      return callback();
    },
    readLibrary: async () => ({ snapshot: snapshot(active, true) }),
    closeDesktop: async () => { closeCalls += 1; return { ...close(), ...(invalidClose ? { allOwnedIdentitiesExited: false } : {}) }; },
    relaunchDesktop: async () => {
      relaunches += 1; launch = 2;
      const barrier = { afterSequence: 14, afterRequestSequence: 24 };
      candidates.push({ sequence: 15, requestSequence: 25, launch: 2, method: 'GET', path: '/studio-bridge/connections',
        httpStatus: 200, body: snapshot(result.databaseB) });
      return { barrier };
    } };
  return { harness, result: () => result, counts, events, polls, writes };
};

const assertRefused = (value) => {
  const result = value.result();
  assert.equal(result.passed, false); assert.equal(result.selectionAttempts.length, 1);
  const attempt = result.selectionAttempts[0];
  assert.equal(attempt.phase, 'failed'); assert.equal(attempt.failure.phase, 'before-click-precondition');
  assert.equal(attempt.failure.terminationReason, 'precondition-refused');
  assert.deepEqual(attempt.barrier, { afterSequence: 10, afterRequestSequence: 20 });
  assert.deepEqual(attempt.ackPoll, { callbackCalls: 0, elapsedMs: null });
  assert.deepEqual(result.selections, []);
  assert.deepEqual([result.firstClose, result.secondLaunch, result.restored, result.query, result.secondClose], [null, null, null, null, null]);
  assert.deepEqual(value.counts(), { clicks: 0, preconditionReads: 1, observerReads: 0, apiCalls: 8, closeCalls: 0, relaunches: 0 });
  assert.equal(value.polls.some((item) => item.label === 'Fresh ordinary selection native PUT'), false);
  assert.deepEqual(value.events, ['precheck-1', 'barrier-1']);
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
  return attempt.precondition;
};

test('WB65 same-value selection records an already-active checkpoint without clicking or polling an acknowledgement', { timeout: 3000 }, async () => {
  const value = fixture({ initial: 'same' });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /precondition refused/u);
  const precondition = assertRefused(value);
  assert.equal(precondition.state, 'target-already-active'); assert.equal(precondition.reason, 'target-already-active');
  assert.equal(precondition.dom.activeIdentityConsistent, true);
});

test('a consistent case variant of the target cannot prove that selecting it changes databases', { timeout: 3000 }, async () => {
  const value = fixture({ initial: 'case' });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /precondition refused/u);
  const precondition = assertRefused(value);
  assert.equal(precondition.state, 'unknown'); assert.equal(precondition.dom.activeMatchesTarget, null);
  assert.equal(precondition.dom.activeIdentityConsistent, true);
});

test('unknown ordinary DOM counts, mixed identity, case mismatch and warning refuse before the original click', { timeout: 4000 }, async () => {
  const cases = [
    (dom) => dom.setCounts(0, 1), (dom) => dom.setCounts(2, 1), (dom) => dom.setCounts(1, 0),
    (dom) => dom.setCounts(1, 2), (dom) => dom.setCounts(17, 1), (dom) => dom.setCounts(1, 17),
    (dom) => { dom.setActive(dom.target); dom.setIdentity('Other'); },
    (dom) => { dom.setActive('Other'); dom.setIdentity(dom.target); },
    (dom) => { dom.setActive(dom.target.toLowerCase()); dom.setIdentity(dom.target); },
    (dom) => dom.setIdentity('DifferentOther'), (dom) => dom.setWarning(true),
    (dom) => dom.setActive(null), (dom) => dom.setIdentity(123),
  ];
  const expires = Date.now() + 2500;
  for (let index = 0; index < cases.length && index < 13 && Date.now() < expires; index += 1) {
    const value = fixture({ beforeRead: cases[index] });
    await assert.rejects(runDatabaseRecoveryScenario(value.harness), /precondition refused/u);
    assert.equal(assertRefused(value).state, 'unknown');
  }
  assert.ok(Date.now() < expires);
});

test('typed projections reject getters and omit arbitrary DOM, SQL, token and error text', { timeout: 3000 }, () => {
  let getterCalls = 0;
  const raw = { ...validObservation(), activeText: 'SECRET_SQL_TOKEN', identityText: 'SECRET_SQL_TOKEN', error: 'SECRET_SQL_TOKEN' };
  const valid = projectDatabaseSelectionPrecondition(raw);
  assert.equal(valid.state, 'change-required'); assert.equal(JSON.stringify(valid).includes('SECRET'), false);
  Object.defineProperty(raw, 'activeIdentityConsistent', { get() { getterCalls += 1; throw new Error('SECRET_GETTER'); } });
  const refused = projectDatabaseSelectionPrecondition(raw);
  assert.equal(refused.state, 'unknown'); assert.equal(refused.dom.activeIdentityConsistent, null); assert.equal(getterCalls, 0);
  assert.equal(projectDatabaseSelectionPrecondition({ ...validObservation(), contractWarningPresent: 'false' }).state, 'unknown');
  assert.equal(projectDatabaseSelectionPrecondition({ ...validObservation(), identityNodeCount: 17 }).dom.identityNodeCount, null);
  assert.equal(JSON.stringify(refused).includes('SECRET'), false);
});

test('a throwing ordinary precondition read becomes a safe zero-action checkpoint', { timeout: 3000 }, async () => {
  const value = fixture({ readFailure: () => { throw new Error('SECRET_DOM_READ'); } });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /precondition refused/u);
  const precondition = assertRefused(value);
  assert.equal(precondition.state, 'unknown'); assert.equal(precondition.reason, 'read-error');
});

test('one pending scenario DOM read stops after its two-second wait without claiming underlying cancellation', { timeout: 4000 }, async () => {
  let rejectRead;
  const value = fixture({ readFailure: () => new Promise((_, reject) => { rejectRead = reject; }) });
  const started = Date.now();
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /precondition refused/u);
  assert.ok(Date.now() - started >= 1900 && Date.now() - started < 3500);
  const precondition = assertRefused(value);
  assert.equal(precondition.reason, 'timeout');
  rejectRead(new Error('SECRET_LATE_REJECTION'));
  await Promise.resolve();
});

test('pending precondition cancellation clears the abort listener and does not start a second read', { timeout: 3000 }, async () => {
  const controller = new AbortController();
  let reads = 0;
  let adds = 0;
  let removes = 0;
  const nativeAdd = controller.signal.addEventListener.bind(controller.signal);
  const nativeRemove = controller.signal.removeEventListener.bind(controller.signal);
  controller.signal.addEventListener = (...args) => { adds += 1; return nativeAdd(...args); };
  controller.signal.removeEventListener = (...args) => { removes += 1; return nativeRemove(...args); };
  const observed = await observeDatabaseSelectionPrecondition(() => {
    reads += 1; controller.abort(new Error('SECRET_CANCEL')); return new Promise(() => {});
  }, { signal: controller.signal });
  assert.equal(observed.state, 'unknown'); assert.equal(observed.reason, 'cancelled');
  assert.equal(reads, 1); assert.equal(adds, 1); assert.equal(removes, 1);
  assert.equal(JSON.stringify(observed).includes('SECRET'), false);
});

test('precondition expiry and prior cancellation acquire no ordinary DOM read', { timeout: 3000 }, async () => {
  let reads = 0;
  const read = () => { reads += 1; return validObservation(); };
  const controller = new AbortController(); controller.abort();
  const expired = await observeDatabaseSelectionPrecondition(read, { deadline: Date.now() - 1 });
  const cancelled = await observeDatabaseSelectionPrecondition(read, { signal: controller.signal });
  const pending = await observeDatabaseSelectionPrecondition(() => { reads += 1; return new Promise(() => {}); }, { deadline: Date.now() + 25 });
  assert.equal(expired.reason, 'timeout'); assert.equal(expired.readCalls, 0);
  assert.equal(cancelled.reason, 'cancelled'); assert.equal(cancelled.readCalls, 0);
  assert.equal(pending.reason, 'timeout'); assert.equal(pending.readCalls, 1); assert.equal(reads, 1);
});

test('scenario cancellation retains the guard error object after the synchronous precondition checkpoint', { timeout: 3000 }, async () => {
  const primary = new Error('SECRET_ORIGINAL_CANCEL');
  const value = fixture({ cancelledCheck: primary });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), (error) => error === primary);
  assert.equal(assertRefused(value).reason, 'guard-refused');
});

test('complete synthetic journey keeps both new checks, both barriers, all seven fresh predicates and original downstream contracts', { timeout: 4000 }, async () => {
  let reads = 0;
  const value = fixture({ inspectPredicates: true, beforeRead: (dom) => {
    reads += 1;
    if (reads === 1) dom.setActive('SECRET_SQL_TOKEN');
    else assert.equal(dom.active, value.result().databaseA);
  } });
  const result = await runDatabaseRecoveryScenario(value.harness);
  assert.equal(result.passed, true); assert.equal(result.selections.length, 2);
  assert.deepEqual(value.events, ['precheck-1', 'barrier-1', 'click-1', 'precheck-2', 'barrier-2', 'click-2']);
  assert.deepEqual(value.counts(), { clicks: 2, preconditionReads: 2, observerReads: 0, apiCalls: 8, closeCalls: 2, relaunches: 1 });
  assert.deepEqual(result.selectionAttempts.map((attempt) => attempt.precondition.state), ['change-required', 'change-required']);
  assert.deepEqual(result.selectionAttempts.map((attempt) => attempt.phase), ['accepted', 'accepted']);
  assert.deepEqual(result.selectionAttempts.map((attempt) => attempt.ackPoll.callbackCalls), [8, 8]);
  const ackPolls = value.polls.filter((item) => item.label === 'Fresh ordinary selection native PUT');
  assert.deepEqual(ackPolls.map((item) => item.options), [
    { attempts: 30, timeoutMs: 20_000, intervalMs: 250 }, { attempts: 30, timeoutMs: 20_000, intervalMs: 250 }]);
  assert.equal(result.restored.acknowledgement.launch, 2); assert.equal(result.query.rendered, 'WB61_B');
  assert.equal(result.query.database, result.databaseB); assert.equal(result.firstClose.fallbackUsed, false);
  assert.equal(result.secondClose.allOwnedIdentitiesExited, true); assert.equal(value.writes.length, 6);
  assert.equal(JSON.stringify(result).includes('SECRET'), false);
});

test('a valid selection precondition cannot substitute for strict owned normal-close acceptance', { timeout: 3000 }, async () => {
  const value = fixture({ invalidClose: true });
  await assert.rejects(runDatabaseRecoveryScenario(value.harness), /strict absence/u);
  assert.equal(value.result().passed, false); assert.equal(value.result().selections.length, 2);
  assert.equal(value.result().secondLaunch, null); assert.equal(value.counts().relaunches, 0);
});
