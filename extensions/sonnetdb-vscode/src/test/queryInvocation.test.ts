import assert from 'node:assert/strict';
import Module from 'node:module';
import test from 'node:test';
import type * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import type { QueryHistoryEntry, QueryHistoryStorage } from '../core/queryHistory';
import type { SonnetDbConnectionProfile, SqlResultSet } from '../core/types';

type Mode = 'query' | 'selection' | 'explain';
type Stage = 'token' | 'list' | 'picker' | 'set-active';
type EditorState = { text: string; selected?: string; offset: number };

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
      timer = setTimeout(() => reject(new Error('Invocation observation exceeded one second.')), 1_000);
    })]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

const handlers = new Map<string, () => unknown>();
const warnings: string[] = [];
const information: string[] = [];
const errors: string[] = [];
const opened: Array<{ language: string; content: string }> = [];
let activeEditor: vscode.TextEditor | undefined;
let renderedHtml = '';
let pickDatabase: () => Promise<string | undefined> = async () => 'original database';
const disposable = { dispose: () => undefined };
const vscodeStub = {
  commands: { registerCommand: (name: string, handler: () => unknown) => {
    handlers.set(name, handler); return disposable;
  } },
  window: {
    get activeTextEditor() { return activeEditor; },
    withProgress: async (_options: unknown, task: () => Promise<unknown>) => task(),
    showWarningMessage: async (message: string) => { warnings.push(message); return undefined; },
    showInformationMessage: async (message: string) => { information.push(message); return undefined; },
    showErrorMessage: async (message: string) => { errors.push(message); return undefined; },
    showQuickPick: async () => pickDatabase(),
    showTextDocument: async () => activeEditor,
    createWebviewPanel: () => ({
      title: '', reveal: () => undefined, onDidDispose: () => disposable,
      webview: {
        get html() { return renderedHtml; },
        set html(value: string) { renderedHtml = value; },
        onDidReceiveMessage: () => disposable,
      },
    }),
  },
  workspace: { openTextDocument: async (options: { language: string; content: string }) => {
    opened.push(options); return {};
  } },
  ViewColumn: { Active: -1, Beside: 2 },
  ProgressLocation: { Window: 10 },
};

// 只在加载真实命令和面板时替换 VS Code 边界，随后恢复 Node loader。
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
} finally {
  loader._load = originalLoad;
}

class DeferredStorage implements QueryHistoryStorage {
  public value: QueryHistoryEntry[] = [];
  public readonly started = deferred<void>();
  public readonly acknowledgement = deferred<void>();

  public get<T>(key: string, defaultValue: T): T {
    assert.equal(key, 'sonnetdb.queryHistory');
    return (this.value ?? defaultValue) as T;
  }

  public async update(key: string, value: unknown): Promise<void> {
    assert.equal(key, 'sonnetdb.queryHistory');
    assert.ok(Array.isArray(value));
    this.started.resolve();
    await this.acknowledgement.promise;
    this.value = value as QueryHistoryEntry[];
  }
}

function editor(state: EditorState): vscode.TextEditor {
  return {
    selection: { get isEmpty() { return state.selected === undefined; }, active: {} },
    document: { getText: (range?: unknown) => range ? state.selected ?? '' : state.text,
      offsetAt: () => state.offset },
  } as unknown as vscode.TextEditor;
}

function fixture(stage: Stage, state?: EditorState) {
  handlers.clear(); warnings.length = 0; information.length = 0; errors.length = 0;
  opened.length = 0; renderedHtml = ''; activeEditor = state ? editor(state) : undefined;
  const reached = deferred<void>();
  const release = deferred<void>();
  const storage = new DeferredStorage();
  const pending: Array<Promise<void>> = [];
  const requests: Array<{ database: string; sql: string }> = [];
  const calls: string[] = [];
  const profile: SonnetDbConnectionProfile = { id: 'fixture', label: 'original connection', kind: 'remote',
    baseUrl: 'http://unused.invalid', defaultDatabase: stage === 'token' ? 'original database' : undefined };
  const context = { subscriptions: [], globalState: storage } as unknown as vscode.ExtensionContext;
  const panel = new QueryResultPanel(context);
  const pause = async (at: Stage) => {
    calls.push(at);
    if (stage === at) { reached.resolve(); await release.promise; }
  };
  const originalExecute = SonnetDbClient.prototype.executeSql;
  const originalList = SonnetDbClient.prototype.listDatabases;
  SonnetDbClient.prototype.executeSql = async (database, sql) => {
    requests.push({ database, sql }); return result();
  };
  SonnetDbClient.prototype.listDatabases = async () => {
    await pause('list');
    return { databases: ['original database'] } as Awaited<ReturnType<SonnetDbClient['listDatabases']>>;
  };
  pickDatabase = async () => { await pause('picker'); return 'original database'; };
  registerRunQueryCommand(context, () => profile, async () => { await pause('token'); return undefined; },
    panel, async () => pause('set-active'));
  return { reached, release, storage, requests, calls, run: (mode: Mode) => {
    assert.equal(pending.length, 0, 'Each fixture owns at most one invocation.');
    const running = command(mode);
    pending.push(running);
    return running;
  }, restore: async () => {
    release.resolve();
    storage.acknowledgement.resolve();
    try {
      await bounded(Promise.allSettled(pending));
    } finally {
      SonnetDbClient.prototype.executeSql = originalExecute;
      SonnetDbClient.prototype.listDatabases = originalList;
      pickDatabase = async () => 'original database';
    }
  } };
}

async function useFixture(task: ReturnType<typeof fixture>, body: () => Promise<void>): Promise<void> {
  let failed = false;
  let failure: unknown;
  try {
    await body();
  } catch (error) {
    failed = true;
    failure = error;
    throw error;
  } finally {
    try {
      await task.restore();
    } catch (cleanupError) {
      if (failed) throw new AggregateError([failure, cleanupError], 'Invocation assertion and bounded drain both failed.');
      throw cleanupError;
    }
  }
}

function result(): SqlResultSet {
  return { columns: ['value'], rows: [[1]], hasColumns: true, error: null,
    end: { type: 'end', rowCount: 1, recordsAffected: 0, elapsedMs: 7 } };
}

function command(mode: Mode): Promise<void> {
  const name = mode === 'query' ? 'runQuery' : mode === 'selection' ? 'runSelection' : 'explainQuery';
  return Promise.resolve(handlers.get(`sonnetdb.${name}`)!()) as Promise<void>;
}

function changeEditor(state?: EditorState): void {
  if (state) { state.text = 'SELECT 999;'; state.selected = 'SELECT 998;'; state.offset = 0; }
  activeEditor = editor({ text: 'SELECT 997;', selected: 'SELECT 996;', offset: 0 });
}

function payload(): { source: { label: string; text: string }; context: { connectionLabel: string; database: string } } {
  const encoded = /id="payload" nonce="[^"]+">([A-Za-z0-9+/=]+)<\/script>/u.exec(renderedHtml)?.[1];
  assert.ok(encoded);
  return JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) as ReturnType<typeof payload>;
}

const invocationCases: Array<{ mode: Mode; stage: Stage }> = [
  { mode: 'query', stage: 'token' }, { mode: 'query', stage: 'list' },
  { mode: 'query', stage: 'picker' }, { mode: 'query', stage: 'set-active' },
  { mode: 'selection', stage: 'token' }, { mode: 'selection', stage: 'list' },
  { mode: 'selection', stage: 'picker' }, { mode: 'selection', stage: 'set-active' },
  { mode: 'explain', stage: 'token' }, { mode: 'explain', stage: 'list' },
  { mode: 'explain', stage: 'picker' }, { mode: 'explain', stage: 'set-active' },
];
const registrationDeadline = Date.now() + 1_000;
for (let index = 0; index < invocationCases.length && index < 12 && Date.now() < registrationDeadline; index += 1) {
  const { mode, stage } = invocationCases[index];
  test(`${mode} keeps invocation SQL while ${stage} waits and awaits real history acknowledgement`, { timeout: 3_000 }, async () => {
    const state: EditorState = { text: 'SELECT 10;\nSELECT 20;', offset: 15,
      selected: mode === 'query' ? undefined : '  SELECT 30;;;  ' };
    const expected = mode === 'query' ? 'SELECT 20;' : mode === 'selection' ? 'SELECT 30;;;' : 'EXPLAIN SELECT 30;';
    const task = fixture(stage, state);
    await useFixture(task, async () => {
      let completed = false;
      const running = task.run(mode).then(() => { completed = true; });
      await bounded(task.reached.promise);
      assert.equal(task.requests.length, 0);
      changeEditor(state);
      task.release.resolve();
      await bounded(task.storage.started.promise);
      assert.deepEqual(task.requests, [{ database: 'original database', sql: expected }]);
      assert.equal(completed, false);
      assert.equal(task.storage.value.length, 0);
      assert.deepEqual(payload().source, { label: 'Query', text: expected });
      assert.deepEqual(payload().context, { connectionLabel: 'original connection', database: 'original database' });
      task.storage.acknowledgement.resolve();
      await bounded(running);
      assert.equal(completed, true);
      assert.equal(task.storage.value.length, 1);
      assert.equal(task.storage.value[0].sql, expected);
      assert.equal(task.storage.value[0].connectionLabel, 'original connection');
      assert.equal(task.storage.value[0].database, 'original database');
      assert.deepEqual(errors, []);
    });
  });
}
assert.equal(invocationCases.length, 12);
assert.ok(Date.now() < registrationDeadline, 'All twelve bounded invocation cases must be registered.');

test('query captures non-empty selection in preference to the current statement', { timeout: 3_000 }, async () => {
  const state: EditorState = { text: 'SELECT 10;\nSELECT 20;', selected: '  SELECT 40;  ', offset: 15 };
  const task = fixture('token', state);
  await useFixture(task, async () => {
    const running = task.run('query');
    await bounded(task.reached.promise); changeEditor(state); task.release.resolve();
    await bounded(task.storage.started.promise);
    assert.deepEqual(task.requests, [{ database: 'original database', sql: 'SELECT 40;' }]);
    task.storage.acknowledgement.resolve(); await bounded(running);
  });
});

const emptyCases: Array<{ mode: Mode; state?: EditorState }> = [
  { mode: 'selection', state: { text: 'SELECT 1;', offset: 0 } },
  { mode: 'selection', state: { text: 'SELECT 1;', selected: '   ', offset: 0 } },
  { mode: 'selection' }, { mode: 'query' }, { mode: 'explain' },
  { mode: 'query', state: { text: '   ', offset: 0 } },
];
const emptyRegistrationDeadline = Date.now() + 1_000;
for (let index = 0; index < emptyCases.length && index < 6 && Date.now() < emptyRegistrationDeadline; index += 1) {
  const { mode, state } = emptyCases[index];
  test(`${mode} preserves invocation missing-SQL behavior, case ${index + 1}`, { timeout: 3_000 }, async () => {
    const task = fixture('token', state);
    await useFixture(task, async () => {
      const running = task.run(mode);
      await bounded(task.reached.promise); changeEditor(state); task.release.resolve(); await bounded(running);
      assert.deepEqual(task.calls, ['token']);
      assert.equal(task.requests.length, 0);
      assert.equal(task.storage.value.length, 0);
      assert.equal(renderedHtml, '');
      if (mode === 'selection') {
        assert.deepEqual(warnings, ['Select the SQL text to run first.']);
        assert.equal(opened.length, 0); assert.deepEqual(information, []);
      } else {
        assert.deepEqual(warnings, []);
        assert.deepEqual(opened, [{ language: 'sql', content: '-- SonnetDB query\nSELECT * FROM measurement LIMIT 100;\n' }]);
        assert.deepEqual(information, ['Enter SQL, then run the current statement or selection.']);
      }
      assert.deepEqual(errors, []);
    });
  });
}
assert.equal(emptyCases.length, 6);
assert.ok(Date.now() < emptyRegistrationDeadline, 'All six bounded missing-SQL cases must be registered.');

test('transport failure for captured SQL uses the existing command error surface', { timeout: 3_000 }, async () => {
  const task = fixture('token', { text: 'SELECT 1;', offset: 0 });
  await useFixture(task, async () => {
    SonnetDbClient.prototype.executeSql = async (_database, sql) => {
      assert.equal(sql, 'SELECT 1;'); throw new Error('captured transport unavailable');
    };
    const running = task.run('query');
    await bounded(task.reached.promise); changeEditor(); task.release.resolve(); await bounded(running);
    assert.deepEqual(errors, ['SonnetDB query failed: captured transport unavailable']);
    assert.equal(task.storage.value.length, 0); assert.equal(renderedHtml, '');
  });
});

test('captured EXPLAIN history acknowledgement rejection stays observable through the command', { timeout: 3_000 }, async () => {
  const task = fixture('token', { text: 'SELECT 1;', offset: 0 });
  await useFixture(task, async () => {
    const running = task.run('explain');
    await bounded(task.reached.promise); changeEditor(); task.release.resolve();
    await bounded(task.storage.started.promise);
    assert.equal(payload().source.text, 'EXPLAIN SELECT 1;');
    task.storage.acknowledgement.reject(new Error('captured history unavailable'));
    await bounded(running);
    assert.deepEqual(errors, ['SonnetDB query failed: captured history unavailable']);
    assert.equal(task.storage.value.length, 0);
  });
});
