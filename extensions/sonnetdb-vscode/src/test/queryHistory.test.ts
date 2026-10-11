import assert from 'node:assert/strict';
import Module from 'node:module';
import test from 'node:test';
import type * as vscode from 'vscode';
import { QueryHistoryEntry, QueryHistoryStorage, QueryHistoryStore } from '../core/queryHistory';
import { SonnetDbClient } from '../core/sonnetdbClient';
import type { SqlResultSet } from '../core/types';

const StorageKey = 'sonnetdb.queryHistory';
const handlers = new Map<string, () => unknown>();
const errors: string[] = [];
const historyPicks: Array<Array<{ label: string; detail: string; description: string }>> = [];
let editorSql = 'SELECT 1;';
let renderedHtml = '';
const disposable = { dispose: () => undefined };
const vscodeStub = {
  commands: {
    registerCommand: (name: string, handler: () => unknown) => { handlers.set(name, handler); return disposable; },
  },
  window: {
    activeTextEditor: { selection: { isEmpty: false }, document: { getText: () => editorSql } },
    withProgress: async (_options: unknown, task: () => Promise<unknown>) => task(),
    showErrorMessage: async (message: string) => { errors.push(message); return undefined; },
    showWarningMessage: async () => undefined,
    showInformationMessage: async () => undefined,
    showQuickPick: async (items: Array<{ label: string; detail: string; description: string }>) => {
      historyPicks.push(items);
      return undefined;
    },
    createWebviewPanel: () => ({
      title: '',
      reveal: () => undefined,
      onDidDispose: () => disposable,
      webview: {
        get html() { return renderedHtml; },
        set html(value: string) { renderedHtml = value; },
        onDidReceiveMessage: () => disposable,
      },
    }),
  },
  ViewColumn: { Beside: 2 },
  ProgressLocation: { Window: 10 },
};

// 此 Node 测试文件独立运行；仅加载两个生产模块时替换 vscode，随后立即恢复 loader。
const loader = Module as unknown as {
  _load: (request: string, parent?: unknown, isMain?: boolean) => unknown;
};
const originalLoad = loader._load;
let QueryResultPanel: typeof import('../panels/queryResultPanel').QueryResultPanel;
let registerRunQueryCommand: typeof import('../commands/runQueryCommand').registerRunQueryCommand;
try {
  loader._load = (request, parent, isMain) => request === 'vscode'
    ? vscodeStub
    : originalLoad.call(Module, request, parent, isMain);
  ({ QueryResultPanel } = require('../panels/queryResultPanel') as typeof import('../panels/queryResultPanel'));
  ({ registerRunQueryCommand } = require('../commands/runQueryCommand') as typeof import('../commands/runQueryCommand'));
} finally {
  loader._load = originalLoad;
}

class DeferredStorage implements QueryHistoryStorage {
  public readonly writes: Array<{ value: QueryHistoryEntry[]; resolve: () => void; reject: (error: Error) => void }> = [];

  public constructor(public value: QueryHistoryEntry[] = []) { }

  public get<T>(key: string, defaultValue: T): T {
    assert.equal(key, StorageKey);
    return (this.value ?? defaultValue) as T;
  }

  public update(key: string, value: unknown): Promise<void> {
    assert.equal(key, StorageKey);
    assert.ok(Array.isArray(value));
    const accepted = value as QueryHistoryEntry[];
    return new Promise<void>((resolve, reject) => {
      this.writes.push({ value: accepted, resolve: () => { this.value = accepted; resolve(); }, reject });
    });
  }
}

function entry(sql: string): QueryHistoryEntry {
  return { id: sql, timestamp: 1_000, sql, connectionLabel: 'original connection', database: 'original database',
    rowCount: 2, elapsedMs: 7, failed: false };
}

function result(): SqlResultSet {
  return { columns: ['value'], rows: [[1], [2]], hasColumns: true, error: null,
    end: { type: 'end', rowCount: 2, recordsAffected: 0, elapsedMs: 7 } };
}

function fixture(storage: DeferredStorage): InstanceType<typeof QueryResultPanel> {
  handlers.clear(); errors.length = 0; historyPicks.length = 0; renderedHtml = ''; editorSql = 'SELECT 1;';
  const context = { globalState: storage, subscriptions: [] } as unknown as vscode.ExtensionContext;
  const panel = new QueryResultPanel(context);
  registerRunQueryCommand(context, () => ({ id: 'fixture', label: 'fixture connection', kind: 'remote',
    baseUrl: 'http://unused.invalid', defaultDatabase: 'fixture database' }), async () => undefined, panel);
  return panel;
}

async function until(condition: () => boolean): Promise<void> {
  const deadline = Date.now() + 1_000;
  for (let attempt = 0; attempt < 64 && Date.now() < deadline; attempt += 1) {
    if (condition()) return;
    await new Promise((resolve) => setTimeout(resolve, 1));
  }
  assert.ok(condition(), 'The bounded deferred-storage observation must become ready.');
}

test('query command and public history wait for the deferred Memento acknowledgement', { timeout: 3_000 }, async () => {
  const storage = new DeferredStorage();
  fixture(storage);
  const originalExecute = SonnetDbClient.prototype.executeSql;
  try {
    SonnetDbClient.prototype.executeSql = async () => result();
    let commandCompleted = false;
    const command = Promise.resolve(handlers.get('sonnetdb.runQuery')!()).then(() => { commandCompleted = true; });
    await until(() => storage.writes.length === 1);
    assert.equal(commandCompleted, false);
    assert.equal(storage.value.length, 0);
    assert.ok(renderedHtml.includes('id="payload"'));
    const history = Promise.resolve(handlers.get('sonnetdb.showQueryHistory')!());
    await new Promise((resolve) => setTimeout(resolve, 1));
    assert.equal(historyPicks.length, 0);
    storage.writes[0].resolve();
    await Promise.all([command, history]);
    assert.equal(commandCompleted, true);
    assert.equal(historyPicks.length, 1);
    assert.equal(historyPicks[0][0].label, 'SELECT 1;');
    assert.equal(historyPicks[0][0].detail, 'fixture connection / fixture database / 7 ms');
    assert.deepEqual(errors, []);
  } finally {
    SonnetDbClient.prototype.executeSql = originalExecute;
  }
});

test('concurrent history preserves accepted snapshots, order, context and the existing fifty-entry cap', { timeout: 3_000 }, async () => {
  const original = Array.from({ length: 51 }, (_value, index) => entry(`old ${index}`));
  const storage = new DeferredStorage(original);
  const history = new QueryHistoryStore(storage);
  const first = entry('current statement');
  const writes = [history.append(first), history.append(entry('exact selection')), history.append(entry('EXPLAIN selection'))];
  first.sql = 'mutated after acceptance';
  first.database = 'mutated database';
  await until(() => storage.writes.length === 1);
  assert.equal(original.length, 51);
  assert.equal(original[0].sql, 'old 0');
  assert.equal(storage.value, original);
  for (let index = 0; index < 3; index += 1) {
    assert.equal(storage.writes.length, index + 1, 'A subsequent update must await the prior acknowledgement.');
    storage.writes[index].resolve();
    await until(() => index === 2 || storage.writes.length === index + 2);
  }
  await Promise.all(writes);
  const actual = await history.read();
  assert.equal(actual.length, 50);
  assert.deepEqual(actual.slice(0, 3).map((value) => value.sql), ['EXPLAIN selection', 'exact selection', 'current statement']);
  assert.ok(actual.slice(0, 3).every((value) => value.database === 'original database'
    && value.connectionLabel === 'original connection' && value.elapsedMs === 7));
  assert.equal(actual[49].sql, 'old 46');
  actual[0].sql = 'mutated returned snapshot';
  assert.equal(storage.value[0].sql, 'EXPLAIN selection');
});

test('a rejected history write is observable and does not poison the already queued next write', { timeout: 3_000 }, async () => {
  const storage = new DeferredStorage();
  const history = new QueryHistoryStore(storage);
  const failed = history.append(entry('rejected'));
  const rejected = assert.rejects(failed, /write rejected/u);
  const next = history.append(entry('next query'));
  await until(() => storage.writes.length === 1);
  storage.writes[0].reject(new Error('write rejected'));
  await rejected;
  await until(() => storage.writes.length === 2);
  assert.equal(storage.value.length, 0);
  storage.writes[1].resolve();
  await next;
  assert.deepEqual((await history.read()).map((value) => value.sql), ['next query']);
});

test('fifty pending history writes reject overflow and release capacity after a rejected write', { timeout: 3_000 }, async () => {
  const storage = new DeferredStorage();
  const history = new QueryHistoryStore(storage);
  const accepted = Array.from({ length: 50 }, (_value, index) => history.append(entry(`query ${index}`)));
  await assert.rejects(history.append(entry('overflow')), { message: 'SonnetDB query history queue is full (50 pending writes).' });
  const firstFailure = assert.rejects(accepted[0], /capacity recovery/u);
  await until(() => storage.writes.length === 1);
  storage.writes[0].reject(new Error('capacity recovery'));
  await firstFailure;
  const recovered = history.append(entry('accepted after failure'));
  const deadline = Date.now() + 1_500;
  for (let index = 1; index < 51 && Date.now() < deadline; index += 1) {
    await until(() => storage.writes.length === index + 1);
    storage.writes[index].resolve();
  }
  assert.equal(storage.writes.length, 51);
  await Promise.all([...accepted.slice(1), recovered]);
  const actual = await history.read();
  assert.equal(storage.writes.length, 51);
  assert.equal(actual.length, 50);
  assert.equal(actual[0].sql, 'accepted after failure');
  assert.equal(actual.some((value) => value.sql === 'overflow'), false);
});

test('non-query result rendering stays synchronous and does not write query history', { timeout: 3_000 }, async () => {
  const storage = new DeferredStorage();
  const panel = fixture(storage);
  assert.equal(panel.showRows('Document preview', ['value'], [[1]], { source: 'document' }, 'preview request'), undefined);
  await new Promise((resolve) => setTimeout(resolve, 1));
  assert.equal(storage.writes.length, 0);
  const encoded = /id="payload" nonce="[^"]+">([A-Za-z0-9+/=]+)<\/script>/u.exec(renderedHtml)?.[1];
  assert.ok(encoded);
  const payload = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) as {
    source: { label: string; text: string }; raw: unknown;
  };
  assert.deepEqual(payload.source, { label: 'Source', text: 'preview request' });
  assert.deepEqual(payload.raw, { source: 'document' });
});

test('query history persistence failure reaches the existing command error surface', { timeout: 3_000 }, async () => {
  const storage = new DeferredStorage();
  fixture(storage);
  const originalExecute = SonnetDbClient.prototype.executeSql;
  try {
    SonnetDbClient.prototype.executeSql = async () => result();
    const command = Promise.resolve(handlers.get('sonnetdb.runQuery')!());
    await until(() => storage.writes.length === 1);
    storage.writes[0].reject(new Error('history persistence unavailable'));
    await command;
    assert.deepEqual(errors, ['SonnetDB query failed: history persistence unavailable']);
    editorSql = 'SELECT 2;';
    const next = Promise.resolve(handlers.get('sonnetdb.runQuery')!());
    await until(() => storage.writes.length === 2);
    storage.writes[1].resolve();
    await next;
    assert.deepEqual(storage.value.map((value) => value.sql), ['SELECT 2;']);
  } finally {
    SonnetDbClient.prototype.executeSql = originalExecute;
  }
});
