import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import { computed, h, markRaw, nextTick, reactive, ref, watch } from 'vue';

const sourcePath = new URL('../src/components/RelationalTableWorkbench.vue', import.meta.url);
const source = readFileSync(sourcePath, 'utf8');
const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/u)[1];
const column = (name, dataType, ordinal, isPrimaryKey = false) => ({
  name, dataType, ordinal, isPrimaryKey, isNullable: false,
});
const table = (name = 'DeviceID:Main') => ({
  name, columns: [column('DeviceID', 'string', 0, true), column('Label', 'string', 1)],
  primaryKey: ['DeviceID'], indexes: [], createdUtc: '',
});
const result = (rows = [['one', 'north-secret']], error = null) => ({
  columns: ['DeviceID', 'Label'], rows, hasColumns: true, error,
  end: { type: 'end', rowCount: rows.length, recordsAffected: -1, elapsedMs: 1 },
});
const deferred = () => {
  let resolve;
  let reject;
  const promise = new Promise((done, failed) => { resolve = done; reject = failed; });
  return { promise, resolve, reject };
};
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
});
const flush = async () => { await nextTick(); await nextTick(); };

async function loadWorkbench(overrides = {}) {
  const props = reactive({ targetDb: 'FactoryDB:East', table: null, tables: [], loading: false, readOnly: false, permissionDenied: false });
  const connection = reactive({ activeProfileId: 'Profile:A', activeBaseUrl: '/', activeProfile: { name: 'Connection A' } });
  const auth = reactive({ api: {}, state: { token: 'session-a', username: 'user-a' } });
  const historyEntries = [];
  const events = [];
  const messages = [];
  const cleanups = [];
  const mounts = [];
  const api = {
    execDataSql: async () => result(),
    execDataSqlBatch: async (_api, _database, statements) => statements.map(() => ({ ...result([]), hasColumns: false, end: { type: 'end', rowCount: 0, recordsAffected: 1, elapsedMs: 1 } })),
    rowsToObjects: (value) => value.rows.map((row) => Object.fromEntries(value.columns.map((name, index) => [name, row[index]]))),
    sqlParameterFromValue: (value) => ({ kind: typeof value === 'number' ? 2 : value === null ? 0 : 1, stringValue: typeof value === 'string' ? value : null, integerValue: typeof value === 'number' ? value : null }),
    ...overrides,
  };
  const names = ['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NSelect', 'NSpace', 'NTag', 'NText'];
  const dependencies = {
    vue: synthetic({ computed, h, reactive, ref, watch: (...args) => { const stop = watch(...args); cleanups.push(stop); return stop; },
      onMounted: (callback) => mounts.push(callback), onBeforeUnmount: (callback) => cleanups.push(callback) }),
    'naive-ui': synthetic({ ...Object.fromEntries(names.map((name) => [name, name])), useMessage: () => Object.fromEntries(['success', 'error', 'info'].map((name) => [name, (text) => messages.push({ name, text })])) }),
    '@/api/sql': synthetic(api),
    '@/stores/auth': synthetic({ useAuthStore: () => auth }),
    '@/stores/connections': synthetic({ useConnectionsStore: () => connection }),
    '@/stores/workbenchHistory': synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => historyEntries.push(entry) }) }),
    '@/utils/writeApproval': synthetic({ createWriteApprovalPlan: (value) => value }),
    '@/utils/sqlWorkbench': synthetic({ formatSqlIdentifier: (value) => `"${value.replaceAll('"', '""')}"` }),
    '@/utils/sqlValue': synthetic({ formatSqlValue: (value) => value === null || value === undefined ? 'NULL' : String(value) }),
    fixture: synthetic({ props, emit: (...args) => events.push(args) }),
  };
  for (const name of ['WorkbenchHistoryDrawer', 'RelationalDdlExport', 'RelationalErDiagram', 'RelationalImportExport', 'RelationalIndexManager', 'RelationalSchemaDesigner', 'WorkbenchResultPanel', 'WorkbenchSectionTabs', 'WriteApprovalPanel']) {
    dependencies[`@/components/${name}.vue`] = synthetic({ default: name });
  }
  const module = new SourceTextModule(stripTypeScriptTypes(`import { props as fixtureProps, emit as fixtureEmit } from 'fixture';
    const defineProps = () => fixtureProps;
    const withDefaults = (value) => value;
    const defineEmits = () => fixtureEmit;
    ${script}
    export { relationState, stateDescriptor, gridRows, tableColumns, rowsResult, latestResult, latestResultSql,
      errorMsg, permissionDenied, loadingRows, confirmBusy, page, pageSize, hasNextPage,
      filterText, sortColumn, sortDirection, insertDraft, editDrafts, pendingOperations, previewPlan,
      loadRows, stageInsert, startEdit, stageUpdate, stageDelete, confirmPendingOperations,
      clearPendingOperations, buildBrowseRequest, openHistoryEntry };`, { mode: 'transform' }), { identifier: sourcePath.href });
  await module.link((specifier) => { assert.ok(dependencies[specifier], `Unexpected dependency: ${specifier}`); return dependencies[specifier]; });
  await module.evaluate({ timeout: 3000 });
  mounts.forEach((callback) => callback());
  return { ...module.namespace, props, connection, auth, historyEntries, events, messages,
    cleanup: () => cleanups.forEach((callback) => callback()) };
}

async function selectTable(workbench) { workbench.props.table = table(); await flush(); }
function stageRow(workbench, value = 'approved') {
  workbench.insertDraft.DeviceID = 'original-id';
  workbench.insertDraft.Label = value;
  workbench.stageInsert();
}

test('Relation preserves original database/resource keys, existing tabs and shared shell planes', { timeout: 5000 }, () => {
  for (const zone of ['toolbar', 'tabs', 'approval', 'center', 'context', 'result', 'status']) assert.ok(source.includes(`data-zone="${zone}"`), zone);
  for (const state of ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent']) assert.ok(source.includes(`${state}: {`), state);
  assert.match(source, /:data-database="targetDb"/u);
  assert.match(source, /:data-resource-key="table\?\.name \?\? ''"/u);
  assert.match(source, /:data-legacy-key="table \? `table:\$\{table.name\}`/u);
  assert.match(source, /readOnly && activeView !== 'ddl'/u);
  for (const key of ['data', 'designer', 'indexes', 'import', 'er', 'ddl']) assert.ok(source.includes(`key: '${key}'`), key);
});

test('six states retain bounded read-only browsing and clear denied rows, schema, result and drafts', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench();
  try {
    assert.equal(workbench.relationState.value, 'empty');
    await selectTable(workbench);
    assert.equal(workbench.relationState.value, 'normal');
    workbench.props.readOnly = true;
    await flush();
    assert.equal(workbench.relationState.value, 'readonly');
    stageRow(workbench);
    assert.equal(workbench.pendingOperations.value.length, 0);
    workbench.props.readOnly = false;
    await flush();
    stageRow(workbench);
    workbench.props.permissionDenied = true;
    assert.equal(workbench.relationState.value, 'permission');
    assert.equal(workbench.gridRows.value.length, 0);
    assert.equal(workbench.tableColumns.value.length, 0);
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.previewPlan.value, null);
    assert.equal(Object.keys(workbench.insertDraft).length, 0);
  } finally { workbench.cleanup(); }
});

test('SELECT request and response stay within the selected page while long fields keep original values', { timeout: 5000 }, async () => {
  const calls = [];
  const rows = Array.from({ length: 220 }, (_, index) => [`id-${index}`, 'x'.repeat(8400)]);
  const workbench = await loadWorkbench({ execDataSql: async (...args) => { calls.push(args); return result(rows); } });
  try {
    await selectTable(workbench);
    workbench.pageSize.value = 200;
    workbench.page.value = 2;
    await workbench.loadRows();
    assert.equal(calls.at(-1)[5], 200);
    assert.equal(calls.at(-1)[3].limit.integerValue, 200);
    assert.equal(calls.at(-1)[3].offset.integerValue, 200);
    assert.match(calls.at(-1)[2], /FROM "DeviceID:Main"/u);
    assert.equal(workbench.gridRows.value.length, 200);
    assert.equal(workbench.gridRows.value[0].Label.length, 8400);
    assert.equal(workbench.latestResult.value.end.truncated, true);
    assert.equal(workbench.relationState.value, 'longContent');
    workbench.pageSize.value = 100000;
    assert.equal(workbench.buildBrowseRequest().parameters.limit.integerValue, 50);
  } finally { workbench.cleanup(); }
});

test('late same-name cross-database and overlapping SELECT results cannot replace current rows', { timeout: 5000 }, async () => {
  const east = deferred();
  const westFirst = deferred();
  let westCalls = 0;
  const workbench = await loadWorkbench({ execDataSql: async (_api, database) => database === 'FactoryDB:East'
    ? east.promise : ++westCalls === 1 ? westFirst.promise : result([['west', 'current-only']]) });
  try {
    workbench.props.table = table();
    workbench.props.targetDb = 'FactoryDB:West';
    const newest = workbench.loadRows();
    await newest;
    east.resolve(result([['east', 'east-secret']]));
    westFirst.resolve(result([['west-old', 'old-secret']]));
    await flush();
    assert.equal(workbench.gridRows.value[0].Label, 'current-only');
    assert.equal(workbench.loadingRows.value, false);
  } finally { workbench.cleanup(); }
});

test('profile, endpoint and session ABA discard late failures even after returning to the original identity', { timeout: 5000 }, async () => {
  for (const key of ['profile', 'endpoint', 'session']) {
    const old = deferred();
    let call = 0;
    const workbench = await loadWorkbench({ execDataSql: () => ++call === 1 ? old.promise : Promise.resolve(result([['new', key]])) });
    try {
      workbench.props.table = table();
      if (key === 'profile') { workbench.connection.activeProfileId = 'Profile:B'; workbench.connection.activeProfileId = 'Profile:A'; }
      if (key === 'endpoint') { workbench.connection.activeBaseUrl = 'https://other.test'; workbench.connection.activeBaseUrl = '/'; }
      if (key === 'session') { workbench.auth.state.token = 'session-b'; workbench.auth.state.token = 'session-a'; }
      await flush();
      old.reject({ response: { status: 403 } });
      await flush();
      assert.equal(workbench.gridRows.value[0].Label, key);
      assert.equal(workbench.permissionDenied.value, false);
    } finally { workbench.cleanup(); }
  }
});

test('ordinary read failures preserve unsubmitted input and denial clears all sensitive payload', { timeout: 5000 }, async () => {
  let failure = null;
  const workbench = await loadWorkbench({ execDataSql: async () => { if (failure) throw failure; return result(); } });
  try {
    await selectTable(workbench);
    workbench.filterText.value = 'kept-filter';
    stageRow(workbench, 'draft-secret');
    workbench.insertDraft.Label = 'unsent-secret';
    failure = new Error('read unavailable');
    await workbench.loadRows();
    assert.equal(workbench.relationState.value, 'error');
    assert.equal(workbench.filterText.value, 'kept-filter');
    assert.equal(workbench.insertDraft.Label, 'unsent-secret');
    assert.equal(workbench.pendingOperations.value.length, 1);
    assert.equal(workbench.gridRows.value.length, 0);
    failure = { response: { status: 403 } };
    await workbench.loadRows();
    assert.equal(workbench.relationState.value, 'permission');
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.pendingOperations.value.length, 0);
    assert.equal(JSON.stringify(workbench.insertDraft).includes('secret'), false);
  } finally { workbench.cleanup(); }
});

test('approvals show captured insert values and update differences without putting parameters into history', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench();
  try {
    await selectTable(workbench);
    assert.equal(workbench.previewPlan.value, null);
    stageRow(workbench, 'approved-value');
    assert.equal(workbench.previewPlan.value.target, 'FactoryDB:East.DeviceID:Main');
    assert.match(workbench.previewPlan.value.items[0].detail, /Label \(string\): approved-value/u);
    workbench.clearPendingOperations();
    const row = workbench.gridRows.value[0];
    workbench.startEdit(row);
    workbench.editDrafts[row.__rowKey].Label = 'changed-value';
    workbench.stageUpdate(row);
    assert.match(workbench.previewPlan.value.items[0].detail, /north-secret → changed-value/u);
    await workbench.confirmPendingOperations();
    assert.equal(workbench.historyEntries[0].status, 'success');
    assert.equal(JSON.stringify(workbench.historyEntries).includes('changed-value'), false);
  } finally { workbench.cleanup(); }
});

test('database, schema and read-only changes synchronously invalidate a previously approved write', { timeout: 5000 }, async () => {
  for (const change of ['database', 'schema', 'readonly']) {
    let writes = 0;
    const workbench = await loadWorkbench({ execDataSqlBatch: async () => { writes += 1; return []; } });
    try {
      await selectTable(workbench);
      stageRow(workbench);
      if (change === 'database') workbench.props.targetDb = 'FactoryDB:West';
      if (change === 'schema') workbench.props.table = { ...table(), columns: [...table().columns, column('NewColumn', 'string', 2)] };
      if (change === 'readonly') workbench.props.readOnly = true;
      await workbench.confirmPendingOperations();
      assert.equal(writes, 0, change);
      assert.equal(workbench.previewPlan.value, null, change);
    } finally { workbench.cleanup(); }
  }
});

test('confirmation sends one captured batch and records its original context after navigation', { timeout: 5000 }, async () => {
  const pending = deferred();
  const calls = [];
  const workbench = await loadWorkbench({ execDataSqlBatch: async (...args) => { calls.push(args); return pending.promise; } });
  try {
    await selectTable(workbench);
    stageRow(workbench, 'captured-value');
    const confirm = workbench.confirmPendingOperations();
    await workbench.confirmPendingOperations();
    workbench.props.targetDb = 'FactoryDB:West';
    workbench.connection.activeProfileId = 'Profile:B';
    pending.resolve(calls[0][2].map(() => ({ ...result([]), end: { type: 'end', rowCount: 0, recordsAffected: 1, elapsedMs: 1 } })));
    await confirm;
    await flush();
    assert.equal(calls.length, 1);
    assert.equal(calls[0][1], 'FactoryDB:East');
    assert.equal(Object.values(calls[0][2][1].parameters).some((value) => value.stringValue === 'captured-value'), true);
    assert.equal(workbench.historyEntries[0].database, 'FactoryDB:East');
    assert.equal(workbench.historyEntries[0].connectionId, 'Profile:A');
    assert.equal(calls[0][3].aborted, true);
    assert.equal(workbench.historyEntries[0].status, 'unknown');
    assert.equal(workbench.historyEntries[0].completeness, 'unknown');
    assert.equal(workbench.messages.filter((item) => item.name === 'success').length, 0);
    assert.equal(workbench.previewPlan.value, null);
  } finally { workbench.cleanup(); }
});

test('missing terminal, invalid response, SQL error and transport failure never succeed or replay', { timeout: 5000 }, async () => {
  for (const outcome of ['missing-end', 'missing-result', 'invalid_sql_response', 'incomplete_sql_response', 'http_408', 'http_504', 'sql-error', 'transport']) {
    let calls = 0;
    const workbench = await loadWorkbench({ execDataSqlBatch: async (_api, _database, statements) => {
      calls += 1;
      if (outcome === 'transport') throw new Error('network disconnected');
      const results = statements.map(() => result([]));
      if (outcome === 'missing-end') results[1].end = null;
      if (outcome === 'missing-result') results.pop();
      if (['invalid_sql_response', 'incomplete_sql_response', 'http_408', 'http_504'].includes(outcome)) {
        results[1].error = { code: outcome, message: 'damaged response' };
        results[1].end = null;
      }
      if (outcome === 'sql-error') {
        results[1].error = { code: 'constraint', message: 'constraint rejected' };
        results[1].end = null;
      }
      return results;
    } });
    try {
      await selectTable(workbench);
      stageRow(workbench);
      await workbench.confirmPendingOperations();
      await workbench.confirmPendingOperations();
      assert.equal(calls, 1, outcome);
      assert.equal(workbench.historyEntries[0].status, outcome === 'sql-error' ? 'error' : 'unknown', outcome);
      assert.equal(workbench.historyEntries[0].completeness, outcome === 'sql-error' ? 'partial' : 'unknown', outcome);
      if (outcome !== 'sql-error') {
        assert.match(workbench.historyEntries[0].summary, /执行结果未知.*服务器终态.*重新预览/u, outcome);
        assert.match(workbench.errorMsg.value, /执行结果未知.*服务器终态.*重新预览/u, outcome);
        assert.equal(workbench.latestResult.value.error.code, 'operation_outcome_unknown', outcome);
      }
      assert.equal(workbench.gridRows.value.length, 0, outcome);
      assert.equal(workbench.messages.filter((item) => item.name === 'success').length, 0, outcome);
      assert.equal(workbench.previewPlan.value, null, outcome);
    } finally { workbench.cleanup(); }
  }
});

test('unmount rejects delayed SELECT rows and releases its loading indicator', { timeout: 5000 }, async () => {
  const read = deferred();
  let calls = 0;
  const workbench = await loadWorkbench({ execDataSql: () => ++calls === 1 ? Promise.resolve(result()) : read.promise });
  await selectTable(workbench);
  const pending = workbench.loadRows();
  workbench.cleanup();
  read.resolve(result([['late', 'late-secret']]));
  await pending;
  assert.equal(workbench.gridRows.value.length, 0);
  assert.equal(workbench.loadingRows.value, false);
});

test('the SQL API http_403 result clears read and write payload and same-identity schema refresh cannot unlock it', { timeout: 5000 }, async () => {
  for (const action of ['read', 'write']) {
    let denied = false;
    let reads = 0;
    const forbidden = { ...result([]), end: null, error: { code: 'http_403', message: 'HTTP 403' } };
    const workbench = await loadWorkbench({
      execDataSql: async () => { reads += 1; return denied ? forbidden : result(); },
      execDataSqlBatch: async () => [forbidden],
    });
    try {
      await selectTable(workbench);
      stageRow(workbench, 'draft-secret');
      workbench.insertDraft.Label = 'unsent-secret';
      if (action === 'read') { denied = true; await workbench.loadRows(); }
      else await workbench.confirmPendingOperations();
      assert.equal(workbench.relationState.value, 'permission', action);
      assert.equal(workbench.latestResult.value, null, action);
      assert.equal(workbench.previewPlan.value, null, action);
      assert.equal(workbench.tableColumns.value.length, 0, action);
      assert.equal(JSON.stringify(workbench.insertDraft).includes('secret'), false, action);
      const before = reads;
      workbench.props.table = { ...table(), columns: [...table().columns, column('NewColumn', 'string', 2)] };
      await flush();
      assert.equal(workbench.permissionDenied.value, true, action);
      assert.equal(reads, before, action);
      assert.equal(workbench.gridRows.value.length, 0, action);
      assert.equal(workbench.previewPlan.value, null, action);
      denied = false;
      workbench.props.permissionDenied = true;
      workbench.props.permissionDenied = false;
      await flush();
      assert.equal(workbench.permissionDenied.value, false, action);
      assert.equal(workbench.gridRows.value[0].DeviceID, 'one', action);
    } finally { workbench.cleanup(); }
  }
});

test('a write completed after unmount records only its captured operation context and never repopulates the page', { timeout: 5000 }, async () => {
  const pending = deferred();
  let submitted;
  const workbench = await loadWorkbench({ execDataSqlBatch: async (_api, database, statements) => {
    submitted = { database, statements };
    return pending.promise;
  } });
  await selectTable(workbench);
  stageRow(workbench);
  const confirm = workbench.confirmPendingOperations();
  workbench.cleanup();
  pending.resolve(submitted.statements.map(() => result([])));
  await confirm;
  assert.equal(submitted.database, 'FactoryDB:East');
  assert.equal(workbench.historyEntries[0].database, 'FactoryDB:East');
  assert.equal(workbench.historyEntries[0].connectionId, 'Profile:A');
  assert.equal(workbench.historyEntries[0].status, 'unknown');
  assert.equal(workbench.historyEntries[0].completeness, 'unknown');
  assert.equal(workbench.gridRows.value.length, 0);
  assert.equal(workbench.latestResult.value, null);
  assert.equal(workbench.messages.filter((item) => item.name === 'success').length, 0);
});

test('an immediate auth change aborts the actual SQL API Axios dispatch before its owned adapter can send a write', { timeout: 5000 }, async () => {
  const sqlSourcePath = new URL('../src/api/sql.ts', import.meta.url);
  const sqlModule = new SourceTextModule(stripTypeScriptTypes(readFileSync(sqlSourcePath, 'utf8'), { mode: 'transform' }), { identifier: sqlSourcePath.href });
  await sqlModule.link((specifier) => { throw new Error(`Unexpected SQL API dependency ${specifier}`); });
  await sqlModule.evaluate({ timeout: 3000 });
  let adapterCalls = 0;
  const api = axios.create({ adapter: async () => {
    adapterCalls += 1;
    throw new Error('the old approved write must not reach the adapter');
  } });
  const workbench = await loadWorkbench({ execDataSqlBatch: sqlModule.namespace.execDataSqlBatch });
  api.interceptors.request.use(async (config) => {
    config.headers.Authorization = `Bearer ${workbench.auth.state.token}`;
    return config;
  });
  try {
    workbench.auth.api = markRaw(api);
    await selectTable(workbench);
    stageRow(workbench);
    const confirm = workbench.confirmPendingOperations();
    workbench.auth.state.token = 'session-b';
    await confirm;
    await workbench.confirmPendingOperations();
    assert.equal(adapterCalls, 0);
    assert.equal(workbench.historyEntries[0].database, 'FactoryDB:East');
    assert.equal(workbench.historyEntries[0].status, 'unknown');
    assert.equal(workbench.historyEntries[0].completeness, 'unknown');
    assert.match(workbench.historyEntries[0].summary, /执行结果未知.*服务器终态/u);
  } finally { workbench.cleanup(); }
});

test('history selection only restores SQL input and never calls the execution APIs', { timeout: 5000 }, async () => {
  let reads = 0;
  let writes = 0;
  const workbench = await loadWorkbench({ execDataSql: async () => { reads += 1; return result(); }, execDataSqlBatch: async () => { writes += 1; return []; } });
  try {
    await selectTable(workbench);
    const before = reads;
    workbench.openHistoryEntry({ command: 'DELETE FROM "DeviceID:Main";' });
    assert.equal(reads, before);
    assert.equal(writes, 0);
    assert.deepEqual(workbench.events.at(-1), ['openSql', 'DELETE FROM "DeviceID:Main";']);
  } finally { workbench.cleanup(); }
});
