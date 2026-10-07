import assert from 'node:assert/strict';
import Module from 'node:module';
import test from 'node:test';
import type * as vscode from 'vscode';
import { QueryHistoryObservation } from '../core/queryHistoryObservation';
import type { QueryHistoryEntry, QueryHistoryStorage } from '../core/queryHistory';
import { SonnetDbClient } from '../core/sonnetdbClient';
import type { SqlResultSet } from '../core/types';

const Secret = 'synthetic-secret-never-in-observation';
const Context = 'synthetic connection / synthetic database';
const activeTimers = new Set<ReturnType<typeof setTimeout>>();

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((accept, fail) => { resolve = accept; reject = fail; });
  return { promise, resolve, reject };
}

async function bounded<T>(promise: Promise<T>): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([promise, new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => reject(new Error('Synthetic observation exceeded one second.')), 1_000);
      activeTimers.add(timer);
    })]);
  } finally {
    if (timer !== undefined) { clearTimeout(timer); activeTimers.delete(timer); }
  }
}

class DeferredStorage implements QueryHistoryStorage {
  public value: QueryHistoryEntry[] = [];
  public readonly started = [deferred<void>(), deferred<void>(), deferred<void>()];
  public readonly writes: Array<{ resolve: () => void; reject: (error: Error) => void }> = [];

  public get<T>(key: string, defaultValue: T): T {
    assert.equal(key, 'sonnetdb.queryHistory');
    return (this.value ?? defaultValue) as T;
  }

  public update(key: string, value: unknown): Promise<void> {
    assert.equal(key, 'sonnetdb.queryHistory');
    assert.ok(Array.isArray(value));
    assert.ok(this.writes.length < 3);
    const accepted = value as QueryHistoryEntry[];
    const index = this.writes.length;
    const acknowledgement = deferred<void>();
    let settled = false;
    this.writes.push({
      resolve: () => {
        if (!settled) { settled = true; this.value = accepted; acknowledgement.resolve(); }
      },
      reject: (error) => {
        if (!settled) { settled = true; acknowledgement.reject(error); }
      },
    });
    this.started[index].resolve();
    return acknowledgement.promise;
  }

  public releaseAll(): void {
    const deadline = Date.now() + 250;
    for (let index = 0; index < this.writes.length && index < 3 && Date.now() < deadline; index += 1) {
      this.writes[index].resolve();
    }
  }
}

interface SyntheticState {
  handlers: Map<string, () => unknown>;
  observer: QueryHistoryObservation;
  sql: string;
  historyItems: unknown;
  notificationCalls: number;
}

let activeState: SyntheticState | undefined;
function state(): SyntheticState { assert.ok(activeState); return activeState; }
const disposable = { dispose: () => undefined };
const vscodeStub = {
  commands: { registerCommand: (name: string, handler: () => unknown) => {
    state().handlers.set(name, handler); return disposable;
  } },
  window: {
    activeTextEditor: { selection: { isEmpty: false }, document: { getText: () => state().sql } },
    withProgress: async (_options: unknown, task: () => Promise<unknown>) => task(),
    showErrorMessage: (message: unknown) => state().observer.forwardError(message, () => {
      state().notificationCalls += 1;
      return Promise.resolve(undefined);
    }),
    showWarningMessage: async () => undefined,
    showInformationMessage: async () => undefined,
    showQuickPick: async (items: unknown) => { state().historyItems = items; return undefined; },
    createWebviewPanel: () => ({
      title: '', reveal: () => undefined, onDidDispose: () => disposable,
      webview: { html: '', onDidReceiveMessage: () => disposable },
    }),
  },
  ViewColumn: { Beside: 2 },
  ProgressLocation: { Window: 10 },
};

// Synthetic Node 边界只在加载真实生产命令/面板时替换，立即恢复 loader。
const loader = Module as unknown as {
  _load: (request: string, parent?: unknown, isMain?: boolean) => unknown;
};
const originalLoad = loader._load;
let QueryResultPanel: typeof import('../panels/queryResultPanel').QueryResultPanel;
let registerRunQueryCommand: typeof import('../commands/runQueryCommand').registerRunQueryCommand;
try {
  loader._load = (request, parent, isMain) => request === 'vscode'
    ? vscodeStub : originalLoad.call(Module, request, parent, isMain);
  ({ QueryResultPanel } = require('../panels/queryResultPanel') as typeof import('../panels/queryResultPanel'));
  ({ registerRunQueryCommand } = require('../commands/runQueryCommand') as typeof import('../commands/runQueryCommand'));
} finally { loader._load = originalLoad; }

function result(): SqlResultSet {
  return { columns: ['value'], rows: [[1]], hasColumns: true, error: null,
    end: { type: 'end', rowCount: 1, recordsAffected: 0, elapsedMs: 7 } };
}

async function useFixture(body: (fixture: {
  state: SyntheticState; storage: DeferredStorage; deadline: number;
  run: (command: string, sql: string) => Promise<void>; history: () => Promise<void>;
}) => Promise<void>): Promise<void> {
  assert.equal(activeState, undefined);
  const deadline = Date.now() + 2_000;
  const current: SyntheticState = { handlers: new Map(), observer: new QueryHistoryObservation(deadline),
    sql: 'SELECT 1;', historyItems: undefined, notificationCalls: 0 };
  const storage = new DeferredStorage();
  const pending: Promise<void>[] = [];
  let queryCommands = 0;
  let historyCommands = 0;
  const originalExecute = SonnetDbClient.prototype.executeSql;
  activeState = current;
  try {
    const context = { subscriptions: [], globalState: storage } as unknown as vscode.ExtensionContext;
    const panel = new QueryResultPanel(context);
    registerRunQueryCommand(context, () => ({ id: 'synthetic', label: 'synthetic connection', kind: 'remote',
      baseUrl: 'http://unused.invalid', defaultDatabase: 'synthetic database' }), async () => undefined, panel);
    SonnetDbClient.prototype.executeSql = async () => result();
    await body({ state: current, storage, deadline,
      run: (command, sql) => {
        assert.ok(queryCommands < 3 && Date.now() < deadline);
        queryCommands += 1;
        current.sql = sql;
        const running = Promise.resolve(current.handlers.get(command)!()) as Promise<void>;
        pending.push(running);
        return running;
      },
      history: async () => {
        assert.ok(historyCommands < 3 && Date.now() < deadline);
        historyCommands += 1;
        current.historyItems = undefined;
        const reading = Promise.resolve(current.handlers.get('sonnetdb.showQueryHistory')!()) as Promise<void>;
        pending.push(reading);
        await reading;
      },
    });
  } finally {
    try {
      storage.releaseAll();
      await bounded(Promise.allSettled(pending));
    } finally {
      SonnetDbClient.prototype.executeSql = originalExecute;
      loader._load = originalLoad;
      current.handlers.clear();
      activeState = undefined;
    }
  }
}

test('synthetic single-item projection is a bounded micro-check with no retained labels', { timeout: 1_000 }, () => {
  const observation = new QueryHistoryObservation(Date.now() + 500);
  assert.equal(observation.begin('current-statement'), true);
  observation.observeHistory([{ label: Secret, detail: Context }], Secret, Context);
  assert.deepEqual(observation.snapshot().phases[0].publicHistory,
    { entryCount: 1, currentLabelMatches: true, currentContextMatches: true });
  assert.equal(JSON.stringify(observation.snapshot()).includes(Secret), false);
});

test('synthetic production promises await ack while second-phase rejection yields a fulfilled command, fixed notification and public history two',
  { timeout: 4_000 }, async () => {
    await useFixture(async (fixture) => {
      const cases = [
        { phase: 'current-statement', command: 'sonnetdb.runQuery', sql: 'SELECT 1;', label: 'SELECT 1;' },
        { phase: 'exact-selection', command: 'sonnetdb.runSelection', sql: 'SELECT 2;', label: 'SELECT 2;' },
        { phase: 'explain', command: 'sonnetdb.explainQuery', sql: 'SELECT 3;', label: 'EXPLAIN SELECT 3;' },
      ];
      for (let index = 0; index < cases.length && index < 3 && Date.now() < fixture.deadline; index += 1) {
        const current = cases[index];
        assert.equal(fixture.state.observer.begin(current.phase), true);
        let completed = false;
        const running = fixture.run(current.command, current.sql).then(() => { completed = true; });
        await bounded(fixture.storage.started[index].promise);
        assert.equal(completed, false, 'Production commands must still await the synthetic Memento acknowledgement.');
        if (index === 1) fixture.storage.writes[index].reject(new Error(Secret));
        else fixture.storage.writes[index].resolve();
        await bounded(running);
        assert.equal(completed, true, 'The existing production catch fulfills even when the ack rejects.');
        fixture.state.observer.endQuery();
        await bounded(fixture.history());
        fixture.state.observer.observeHistory(fixture.state.historyItems, current.label, Context);
      }
      const snapshot = fixture.state.observer.snapshot();
      assert.equal(snapshot.phases.length, 3);
      assert.deepEqual(snapshot.phases.map((phase) => phase.publicHistory.entryCount), [1, 1, 2]);
      assert.deepEqual(snapshot.phases.map((phase) => phase.publicHistory.currentLabelMatches), [true, false, true]);
      assert.deepEqual(snapshot.phases.map((phase) => phase.notifications.queryFailed), [0, 1, 0]);
      assert.equal(snapshot.notificationCount, 1);
      assert.equal(fixture.state.notificationCalls, 1);
      assert.equal(snapshot.persistenceAcknowledgementVerified, false);
      assert.equal(snapshot.causalityEstablished, false);
      assert.equal(JSON.stringify(snapshot).includes(Secret), false);
      const finalHistory = fixture.state.historyItems as Array<{ label: string }>;
      assert.deepEqual(finalHistory.map((item) => item.label), ['EXPLAIN SELECT 3;', 'SELECT 1;']);
      assert.equal(fixture.storage.value.length, 2);
    });
    assert.equal(activeTimers.size, 0);
  });

test('synthetic successful public history waits for the deferred production acknowledgement', { timeout: 4_000 }, async () => {
  await useFixture(async (fixture) => {
    fixture.state.observer.begin('current-statement');
    const running = fixture.run('sonnetdb.runQuery', 'SELECT 1;');
    await bounded(fixture.storage.started[0].promise);
    let historyCompleted = false;
    const history = fixture.history().then(() => { historyCompleted = true; });
    let timer: ReturnType<typeof setTimeout> | undefined;
    try {
      await bounded(new Promise<void>((resolve) => {
        timer = setTimeout(resolve, 1);
        activeTimers.add(timer);
      }));
      assert.equal(historyCompleted, false);
      assert.equal(fixture.state.historyItems, undefined);
      fixture.storage.writes[0].resolve();
      await bounded(Promise.all([running, history]));
      fixture.state.observer.endQuery();
      fixture.state.observer.observeHistory(fixture.state.historyItems, 'SELECT 1;', Context);
      assert.deepEqual(fixture.state.observer.snapshot().phases[0].publicHistory,
        { entryCount: 1, currentLabelMatches: true, currentContextMatches: true });
    } finally {
      if (timer !== undefined) { clearTimeout(timer); activeTimers.delete(timer); }
    }
  });
  assert.equal(activeTimers.size, 0);
});

test('synthetic observation bounds three phases, sixteen notifications and fifty history entries', { timeout: 1_000 }, () => {
  const deadline = Date.now() + 500;
  const observation = new QueryHistoryObservation(deadline);
  assert.equal(observation.begin('exact-selection'), false);
  assert.equal(observation.begin('current-statement'), true);
  for (let index = 0; index < 16 && Date.now() < deadline; index += 1) observation.observeError('SonnetDB query failed: secret');
  observation.observeError('other secret');
  observation.observeHistory(new Array(50).fill({ label: 'expected', detail: Context }), 'expected', Context);
  assert.equal(observation.begin('exact-selection'), true);
  observation.observeHistory(new Array(51).fill({ label: Secret, detail: Secret }), Secret, Secret);
  assert.equal(observation.begin('explain'), true);
  observation.observeHistory(undefined, Secret, Secret);
  assert.equal(observation.begin('explain'), false);
  const snapshot = observation.snapshot();
  assert.equal(snapshot.notificationCount, 16);
  assert.equal(snapshot.notificationOverflow, true);
  assert.equal(snapshot.phases[0].notifications.overflow, true);
  assert.equal(snapshot.phases[0].publicHistory.entryCount, 50);
  assert.equal(snapshot.phases[1].publicHistory.entryCount, 'unknown');
  assert.equal(snapshot.phases[2].publicHistory.entryCount, 'unknown');
  assert.equal(new QueryHistoryObservation(0).begin('current-statement'), false);
});

test('synthetic illegal inputs, getters and throwing proxies never enter the fixed observation projection', { timeout: 1_000 }, () => {
  let getterCalls = 0;
  const item = { get label() { getterCalls += 1; return Secret; }, get detail() { getterCalls += 1; return Secret; } };
  const observation = new QueryHistoryObservation(Date.now() + 500);
  observation.begin('current-statement');
  observation.observeError({ get message() { getterCalls += 1; return Secret; }, toString: () => { getterCalls += 1; return Secret; } });
  observation.observeError('unrelated private notification');
  observation.observeHistory([item], Secret, Secret);
  observation.begin('exact-selection');
  const items = new Proxy([item], { getOwnPropertyDescriptor: () => { throw new Error(Secret); } });
  observation.observeHistory(items, Secret, Secret);
  observation.begin('explain');
  const indexed: unknown[] = [];
  Object.defineProperty(indexed, '0', { get: () => { getterCalls += 1; return item; } });
  observation.observeHistory(indexed, Secret, Secret);
  const snapshot = observation.snapshot();
  assert.equal(getterCalls, 0);
  assert.equal(snapshot.phases[0].notifications.unknown, 1);
  assert.equal(snapshot.phases[0].notifications.other, 1);
  assert.equal(snapshot.phases[0].publicHistory.currentLabelMatches, 'unknown');
  assert.equal(snapshot.phases[1].publicHistory.entryCount, 'unknown');
  assert.equal(snapshot.phases[2].publicHistory.currentLabelMatches, 'unknown');
  assert.equal(JSON.stringify(snapshot).includes(Secret), false);
});

test('synthetic error forwarding preserves return identity, original rejection and observation failure isolation', { timeout: 1_000 }, async () => {
  const observation = new QueryHistoryObservation(Date.now() + 500);
  observation.begin('current-statement');
  const returned = { get then() { throw new Error(Secret); } };
  assert.equal(observation.forwardError('SonnetDB query failed: private', () => returned), returned);
  const failure = new Error(Secret);
  assert.throws(() => observation.forwardError(undefined, () => { throw failure; }), (error) => error === failure);
  const rejected = Promise.reject(failure);
  assert.equal(observation.forwardError('other', () => rejected), rejected);
  await assert.rejects(rejected, (error) => error === failure);
  const originalObserve = observation.observeError;
  try {
    observation.observeError = () => { throw failure; };
    assert.equal(observation.forwardError(undefined, () => returned), returned);
  } finally { observation.observeError = originalObserve; }
  assert.equal(observation.snapshot().notificationObservationUnknown, true);
  assert.equal(JSON.stringify(observation.snapshot()).includes(Secret), false);
});

test('synthetic failing fixture drains its owned write and restores SDK, loader and finite timers', { timeout: 4_000 }, async () => {
  const originalExecute = SonnetDbClient.prototype.executeSql;
  let storage: DeferredStorage | undefined;
  await assert.rejects(useFixture(async (fixture) => {
    storage = fixture.storage;
    fixture.state.observer.begin('current-statement');
    fixture.run('sonnetdb.runQuery', 'SELECT 1;');
    await bounded(fixture.storage.started[0].promise);
    throw new Error('synthetic fixture assertion');
  }), /synthetic fixture assertion/u);
  assert.equal(storage?.value.length, 1);
  assert.equal(SonnetDbClient.prototype.executeSql, originalExecute);
  assert.equal(loader._load, originalLoad);
  assert.equal(activeState, undefined);
  assert.equal(activeTimers.size, 0);
});
