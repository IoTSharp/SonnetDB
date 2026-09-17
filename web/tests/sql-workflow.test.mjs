import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';

const sourceRoot = fileURLToPath(new URL('../src/', import.meta.url));

// Execute production modules; only Vue reactivity is replaced in this dependency-free check.
async function loadWorkflow() {
  const watchers = new Set();
  const disposers = [];
  const flush = () => {
    for (const watcher of [...watchers]) {
      const next = watcher.source();
      if (next.some((value, index) => value !== watcher.previous[index])) {
        watcher.previous = next;
        watcher.callback();
      }
    }
  };
  const ref = (initial) => {
    let value = initial;
    return { get value() { return value; }, set value(next) { value = next; flush(); } };
  };
  const computed = (input) => typeof input === 'function'
    ? { get value() { return input(); } }
    : { get value() { return input.get(); }, set value(next) { input.set(next); flush(); } };
  const vue = new SyntheticModule(['ref', 'computed', 'watch', 'onScopeDispose'], function () {
    this.setExport('ref', ref);
    this.setExport('computed', computed);
    this.setExport('watch', (source, callback) => {
      const watcher = { source, callback, previous: source() };
      watchers.add(watcher);
      return () => watchers.delete(watcher);
    });
    this.setExport('onScopeDispose', (callback) => disposers.push(callback));
  });
  const consoleStore = new SyntheticModule(['CONTROL_PLANE_KEY'], function () {
    this.setExport('CONTROL_PLANE_KEY', '__control_plane__');
  });
  const modules = new Map();
  const allowed = new Set([
    'composables/useSqlExecution.ts', 'api/sql.ts', 'api/sqlSplit.ts', 'api/sqlMeta.ts',
    'api/sqlFormat.ts', 'utils/sqlWorkbench.ts', 'utils/writeApproval.ts',
  ].map((path) => resolve(sourceRoot, path)));
  const load = (path) => {
    assert.ok(allowed.has(path), `Unexpected dependency: ${path}`);
    if (!modules.has(path)) modules.set(path, new SourceTextModule(
      stripTypeScriptTypes(readFileSync(path, 'utf8'), { mode: 'transform' }), { identifier: path },
    ));
    return modules.get(path);
  };
  const entry = load(resolve(sourceRoot, 'composables/useSqlExecution.ts'));
  await entry.link((specifier, parent) => {
    if (specifier === 'vue') return vue;
    if (specifier === '@/stores/sqlConsole') return consoleStore;
    const path = specifier.startsWith('@/')
      ? resolve(sourceRoot, `${specifier.slice(2)}.ts`)
      : resolve(dirname(parent.identifier), `${specifier}.ts`);
    return load(path);
  });
  await entry.evaluate({ timeout: 5000 });
  return {
    useSqlExecution: entry.namespace.useSqlExecution,
    sqlApi: modules.get(resolve(sourceRoot, 'api/sql.ts')).namespace,
    ref, computed, flush, dispose: () => disposers.forEach((callback) => callback()),
  };
}

const ndjson = (...records) => records.map((record) => JSON.stringify(record)).join('\n');
const meta = { type: 'meta', columns: ['value'] };
const end = { type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 2 };
const response = () => ({ data: ndjson(meta, [1], end), headers: { 'content-type': 'application/x-ndjson' }, status: 200 });

test('SQL parser rejects damaged, empty and incomplete results without discarding completed batches', { timeout: 5000 }, async () => {
  const { sqlApi } = await loadWorkflow();
  assert.equal(sqlApi.parseNdjson(ndjson(meta, [1], end)).error, null);
  assert.equal(sqlApi.parseNdjson(ndjson({ type: 'end', rowCount: 0, recordsAffected: 3, elapsedMs: 1 })).end.recordsAffected, 3);
  assert.equal(sqlApi.parseNdjson(ndjson({ type: 'error', code: 'forbidden', message: 'denied' })).error.code, 'forbidden');
  for (const body of ['', ndjson(meta, [1]), ndjson(meta, [1], meta)]) {
    assert.equal(sqlApi.parseNdjson(body).error.code, 'incomplete_sql_response');
  }
  for (const body of [`${ndjson(meta)}\n[broken`, ndjson([1], end), ndjson(null, end), ndjson({ unknown: true }, end)]) {
    assert.equal(sqlApi.parseNdjson(body).error.code, 'invalid_sql_response');
  }
  const batch = sqlApi.parseNdjsonResults(`${ndjson(meta, [1], end)}\n${ndjson(meta, [2])}`);
  assert.equal(batch.length, 2);
  assert.equal(batch[0].error, null);
  assert.equal(batch[1].error.code, 'incomplete_sql_response');
  assert.equal(sqlApi.parseNdjson(`${ndjson(meta, [1], end)}\n{broken`).error.code, 'invalid_sql_response');
  assert.equal(sqlApi.parseNdjson(ndjson(meta, [1], end, meta, [2], end)).error.code, 'invalid_sql_response');
  for (const body of [ndjson({ type: 'end' }), ndjson(meta, [1, 2], end), ndjson(meta, [1], { ...end, rowCount: 2 })]) {
    assert.equal(sqlApi.parseNdjson(body).error.code, 'invalid_sql_response');
  }
  const damagedApi = { post: async () => ({ ...response(), data: `${ndjson(meta, [1], end)}\n{broken` }) };
  assert.equal((await sqlApi.execDataSql(damagedApi, 'alpha', 'SELECT 1')).error.code, 'invalid_sql_response');
});

async function fixture(sql = 'SELECT 1; SELECT 2') {
  const runtime = await loadWorkflow();
  const tabs = [{ id: 'a', db: 'alpha', sql, title: 'A' }, { id: 'b', db: 'beta', sql: 'SELECT 9', title: 'B' }];
  const active = runtime.ref(tabs[0]);
  const history = [];
  const calls = [];
  let complete;
  const api = { defaults: { baseURL: 'http://first.invalid' }, post: async (url, body, options) => {
    calls.push({ url, body, options });
    if (calls.length === 1) return new Promise((resolvePromise, reject) => {
      complete = (value = response()) => resolvePromise(value);
      options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
    });
    return response();
  } };
  const auth = { api, state: { token: 'first-token' }, isSuperuser: true };
  const connections = { activeProfileId: 'first', activeProfile: { name: 'First' }, activeBaseUrl: api.defaults.baseURL };
  const store = {
    tabs,
    patchTab(id, patch) { Object.assign(tabs.find((tab) => tab.id === id), patch); },
    setTabResults(id, results, summary, errorMsg, ranOnce) { this.patchTab(id, { results, summary, errorMsg, ranOnce }); },
  };
  const execution = runtime.useSqlExecution({
    auth, connections, sqlConsole: store, workbenchHistory: { record: (entry) => history.push(entry) },
    activeTab: active, targetDb: runtime.computed({ get: () => active.value.db, set: (db) => { active.value.db = db; } }),
    sql: runtime.computed({ get: () => active.value.sql, set: (value) => { active.value.sql = value; } }),
    databases: runtime.ref(['alpha', 'beta']), selectedMeasurement: runtime.ref(null), message: {},
    reloadDbs: async () => {}, loadSchema: async () => {}, setWorkbenchTool() {},
  });
  return { ...runtime, tabs, active, auth, connections, calls, history, execution, complete: (value) => complete(value) };
}

test('Switching tabs aborts an approved batch and preserves original history and result ownership', { timeout: 5000 }, async () => {
  const f = await fixture('INSERT INTO events VALUES (1); INSERT INTO events VALUES (2)');
  await f.execution.run();
  const running = f.execution.confirmPreview();
  assert.equal(f.calls.length, 1);
  f.active.value = f.tabs[1];
  await running;
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].options.signal.aborted, true);
  assert.equal(f.history[0].database, 'alpha');
  assert.equal(f.history[0].connectionId, 'first');
  assert.equal(f.tabs[0].results[0].result.error.code, 'sql_execution_cancelled');
  assert.equal(f.tabs[1].results, undefined);
});

test('Connection changes stop later statements even when the HTTP adapter completes after cancellation', { timeout: 5000 }, async () => {
  const f = await fixture();
  const running = f.execution.run();
  f.auth.api.defaults.baseURL = 'http://second.invalid';
  f.connections.activeProfileId = 'second';
  f.connections.activeProfile.name = 'Second';
  f.complete();
  await running;
  assert.equal(f.calls.length, 1);
  assert.ok(f.history.every((entry) => entry.connectionId === 'first' && entry.connectionName === 'First'));
  assert.equal(f.tabs[0].results[1].result.error.code, 'sql_execution_cancelled');
});

test('USE changes only the executing context and keeps its following statements valid', { timeout: 5000 }, async () => {
  const f = await fixture('USE beta; SELECT current_database(); SELECT 1');
  const running = f.execution.run();
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, '/v1/db/beta/sql');
  f.complete();
  await running;
  assert.equal(f.tabs[0].results.length, 3);
  assert.equal(f.tabs[0].results[1].result.rows[0][0], 'beta');
  assert.equal(f.tabs[0].errorMsg, '');
});

test('Disposal cancels pending HTTP and a duplicate run cannot dispatch a second batch', { timeout: 5000 }, async () => {
  const f = await fixture();
  const running = f.execution.run();
  await f.execution.run();
  assert.equal(f.calls.length, 1);
  f.dispose();
  await running;
  assert.equal(f.calls.length, 1);
  assert.equal(f.tabs[0].results[0].result.error.code, 'sql_execution_cancelled');
});

test('An approval from another connection or authentication state cannot execute', { timeout: 5000 }, async () => {
  const f = await fixture('INSERT INTO events VALUES (1)');
  await f.execution.run();
  f.connections.activeProfileId = 'second';
  f.auth.api.defaults.baseURL = 'http://second.invalid';
  await f.execution.confirmPreview();
  assert.equal(f.calls.length, 0);
  assert.equal(f.execution.previewIsStale.value, true);
});

test('A single-database transaction uses one batch HTTP request and maps each result', { timeout: 5000 }, async () => {
  const f = await fixture('-- atomic insert\nBEGIN; INSERT INTO events VALUES (1); COMMIT');
  await f.execution.run();
  const running = f.execution.confirmPreview();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].url, '/v1/db/alpha/sql/batch');
  assert.equal(f.calls[0].body.statements.length, 3);
  f.complete({ ...response(), data: ndjson(
    { ...end, rowCount: 0, recordsAffected: 0 },
    { ...end, rowCount: 0, recordsAffected: 1 },
    { ...end, rowCount: 0, recordsAffected: 0 },
  ) });
  await running;
  assert.equal(f.calls.length, 1);
  assert.equal(f.tabs[0].results.length, 3);
  assert.equal(f.tabs[0].results[1].result.end.recordsAffected, 1);
  assert.equal(f.tabs[0].errorMsg, '');
});

test('Mixed-database and savepoint transaction scripts are rejected before sending', { timeout: 5000 }, async () => {
  for (const script of ['BEGIN; USE beta; COMMIT', 'BEGIN; SAVEPOINT first; COMMIT', 'BEGIN; CREATE DATABASE example; COMMIT']) {
    const f = await fixture(script);
    await f.execution.run();
    await f.execution.confirmPreview();
    assert.equal(f.calls.length, 0);
    assert.equal(f.tabs[0].results[0].result.error.code, 'unsupported_transaction_batch');
  }
});

test('A transaction failure stops result mapping and preserves the server error', { timeout: 5000 }, async () => {
  const f = await fixture('BEGIN; INSERT INTO events VALUES (1); ROLLBACK');
  await f.execution.run();
  const running = f.execution.confirmPreview();
  f.complete({ ...response(), data: ndjson(
    { ...end, rowCount: 0, recordsAffected: 0 },
    { type: 'error', code: 'constraint_error', message: 'duplicate key' },
  ) });
  await running;
  assert.equal(f.calls.length, 1);
  assert.equal(f.tabs[0].results.length, 2);
  assert.equal(f.tabs[0].results[1].result.error.code, 'constraint_error');
});
