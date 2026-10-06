import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { createContext, SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';

const source = readFileSync(new URL('../src/components/MeasurementWorkbench.vue', import.meta.url), 'utf8');
const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/u)[1];
const read = (name) => readFileSync(new URL(`../src/${name}.ts`, import.meta.url), 'utf8');
const measurement = (name = 'DeviceID:Original') => ({ name, columns: [
  { name: 'time', role: 'Time', dataType: 'TIMESTAMP' }, { name: 'SiteID', role: 'Tag', dataType: 'STRING' }, { name: 'ValueID', role: 'Field', dataType: 'DOUBLE' },
] });
const result = (rows = [[10, 'site', 7]], error = null) => ({ columns: ['time', 'SiteID', 'ValueID'], rows, hasColumns: true, error,
  end: error ? null : { type: 'end', rowCount: rows.length, recordsAffected: -1, elapsedMs: 1 } });
const terminal = () => ({ columns: [], rows: [], hasColumns: false, error: null, end: { type: 'end', rowCount: 0, recordsAffected: 1, elapsedMs: 1 } });
const deferred = () => { let resolve; const promise = new Promise((done) => { resolve = done; }); return { promise, resolve }; };
const flush = async () => { await Promise.resolve(); await vue.nextTick(); await Promise.resolve(); await vue.nextTick(); };
const wire = (results) => results.flatMap((value) => [
  ...(value.hasColumns ? [JSON.stringify({ type: 'meta', columns: value.columns })] : []), ...value.rows.map((row) => JSON.stringify(row)),
  ...(value.error ? [JSON.stringify({ type: 'error', ...value.error })] : value.end ? [JSON.stringify(value.end)] : []),
]).join('\n');

async function fixture({ autoReads = true, honorAbort = true, actualAxios = false } = {}) {
  const calls = []; const history = []; const notices = []; const exports = []; const cleanups = []; const events = [];
  const clock = { now: 0, next: 0, timers: new Map() };
  const schedule = (callback, delay) => { const id = ++clock.next; clock.timers.set(id, { at: clock.now + delay, callback }); return id; };
  const cancel = (id) => clock.timers.delete(id);
  class ClockDate extends Date { static now() { return clock.now; } }
  const context = createContext({ Date: ClockDate, performance: { now: () => clock.now }, AbortController, setTimeout: schedule, clearTimeout: cancel,
    window: { setTimeout: schedule, clearTimeout: cancel }, console });
  const synthetic = (values) => new SyntheticModule(Object.keys(values), function () {
    const entries = Object.entries(values);
    assert.ok(entries.length <= 256, 'Fixture module exceeds the 256-export budget');
    const deadline = Date.now() + 1000;
    let index = 0;
    for (; index < entries.length && Date.now() < deadline; index += 1) this.setExport(...entries[index]);
    assert.equal(index, entries.length, 'Fixture module export binding exceeded its wall-clock budget');
  }, { context });
  const props = vue.reactive({ targetDb: 'North:DB', measurement: null, measurements: [], tables: [], loading: false, readOnly: false, permissionDenied: false });
  const connections = vue.reactive({ activeProfileId: 'Profile:Original', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'Original profile' } });
  const auth = vue.reactive({ state: { token: 'first' }, api: null });
  const post = (url, body, config = {}) => {
    const call = { url, body, signal: config.signal, batch: url.endsWith('/batch') }; calls.push(call);
    return new Promise((resolve, reject) => {
      const done = () => config.signal?.removeEventListener('abort', abort);
      const abort = () => { if (honorAbort) { done(); reject(Object.assign(new Error('cancelled-secret'), { code: 'ERR_CANCELED' })); } };
      call.results = (values) => { done(); resolve({ status: 200, data: wire(values), headers: { 'content-type': 'application/x-ndjson' } }); };
      call.error = (status, value = { message: 'server-secret' }) => { done(); resolve({ status, data: JSON.stringify(value), headers: { 'content-type': 'application/json' } }); };
      call.reject = (error) => { done(); reject(error); };
      config.signal?.addEventListener('abort', abort, { once: true });
      if (config.signal?.aborted) abort(); else if (!call.batch && autoReads) call.results([result()]);
    });
  };
  const makeActualClient = () => vue.markRaw(axios.create({ baseURL: 'http://first.invalid', adapter: async (config) => {
    calls.push({ url: config.url, body: JSON.parse(config.data), signal: config.signal, token: config.headers.Authorization, batch: config.url.endsWith('/batch') });
    return { config, status: 200, statusText: 'OK', data: wire([result()]), headers: { 'content-type': 'application/x-ndjson' } };
  } }));
  if (actualAxios) {
    const client = new SourceTextModule(stripTypeScriptTypes(read('api/client').replace('import.meta.env.BASE_URL', "'http://first.invalid'"), { mode: 'transform' }), { context });
    await client.link(() => synthetic({ default: { create(options) { const value = makeActualClient(); value.defaults.timeout = options.timeout; return value; } } }));
    await client.evaluate({ timeout: 3000 }); auth.api = vue.markRaw(client.namespace.createApiClient(() => auth.state?.token ?? null));
  } else auth.api = vue.markRaw({ defaults: vue.reactive({ baseURL: 'http://first.invalid' }), post });
  const native = (name) => new SourceTextModule(stripTypeScriptTypes(read(name), { mode: 'transform' }), { context, identifier: name });
  const modules = new Map([
    ['vue', synthetic({ ...vue, watch: (...args) => { const stop = vue.watch(...args); cleanups.push(stop); return stop; }, onBeforeUnmount: (callback) => cleanups.push(callback) })],
    ['naive-ui', synthetic({ ...Object.fromEntries(['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NProgress', 'NRadioButton', 'NRadioGroup', 'NSelect', 'NSpace', 'NTag', 'NText'].map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'error'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['lucide-vue-next', synthetic({ Pause: {}, Play: {} })], ['@/api/sql', native('api/sql')], ['@/utils/measurementImport', native('utils/measurementImport')],
    ['@/utils/relationalImportExport', native('utils/relationalImportExport')], ['@/utils/sqlWorkbench', synthetic({ formatSqlIdentifier: (value) => `"${value.replaceAll('"', '""')}"` })],
    ['@/utils/sqlValue', synthetic({ formatSqlValue: (value) => String(value ?? 'NULL') })], ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (value) => value })],
    ['@/utils/resultExport', synthetic({ buildCsv: (rows) => JSON.stringify(rows), buildJson: (rows) => JSON.stringify(rows), safeFileStem: (name) => name, saveTextFile: async (...args) => { exports.push(args); return 'saved'; } })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })], ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (value) => history.push(value) }) })], ['fixture', synthetic({ props, emit: (...args) => events.push(args) })],
  ]);
  for (const name of ['SqlResultChart', 'WorkbenchHistoryDrawer', 'WorkbenchSectionTabs', 'WriteApprovalPanel']) modules.set(`@/components/${name}.vue`, synthetic({ default: {} }));
  const component = new SourceTextModule(stripTypeScriptTypes(`import { props as fixtureProps, emit as fixtureEmit } from 'fixture';
    const defineProps = () => fixtureProps; const withDefaults = (value) => value; const defineEmits = () => fixtureEmit;
    ${script}
    export { measurementState, columns, pointRows, pointResult, monitorResult, monitorRows, monitorLoading, loadingPoints,
      pointDraft, editorOpen, importText, importParsed, importProgress, pendingOperations, approvalPlan, writeBusy,
      errorMessage, monitorError, permissionDenied, activeView, pointLimit, monitorTarget, monitorModel, monitorLimit, monitorInterval,
      monitorRunning, loadPoints, refreshMonitor, openNewPoint, stagePoint, stageDelete, confirmPendingOperations, onFileSelected,
      analyzeImport, stageImport, stopImport, toggleMonitor, exportVisiblePoints, openSchemaSql };`, { mode: 'transform' }), { context });
  await component.link((name) => { assert.ok(modules.has(name), `Unexpected module ${name}`); return modules.get(name); });
  await component.evaluate({ timeout: 3000 });
  const select = async (value = measurement()) => { props.measurement = value; props.measurements = [value]; await flush(); };
  const latest = (batch = false) => calls.filter((call) => call.batch === batch).at(-1);
  const stage = () => { component.namespace.openNewPoint(); Object.assign(component.namespace.pointDraft, { time: 20, SiteID: 'draft-site', ValueID: 8 }); component.namespace.stagePoint(); };
  const importRows = (count) => { component.namespace.importText.value = 'time,SiteID,ValueID\n' + Array.from({ length: count }, (_, index) => `${index + 100},site,${index}`).join('\n'); component.namespace.analyzeImport(); component.namespace.stageImport(); };
  const advance = async (milliseconds) => {
    const end = clock.now + milliseconds; const deadline = Date.now() + 2000;
    for (let index = 0; index < 50 && Date.now() < deadline; index += 1) {
      const next = [...clock.timers.entries()].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!next) break; clock.now = next[1].at; clock.timers.delete(next[0]); next[1].callback(); await flush();
    }
    clock.now = end; await flush();
  };
  return { ...component.namespace, props, auth, connections, calls, history, notices, exports, events, clock, select, latest, stage, importRows, advance, makeActualClient,
    dispose: () => {
      assert.ok(cleanups.length <= 256, 'Fixture cleanup exceeds the 256-callback budget');
      const deadline = Date.now() + 1000;
      let index = 0;
      try {
        for (; index < cleanups.length && Date.now() < deadline; index += 1) cleanups[index]();
        assert.equal(index, cleanups.length, 'Fixture cleanup exceeded its wall-clock budget');
      } finally { clock.timers.clear(); }
    } };
}

test('runtime shell, original SQL names, six states and readonly export retain existing workflow', { timeout: 5000 }, async () => {
  const f = await fixture(); try {
    for (const zone of ['toolbar', 'tabs', 'approval', 'center', 'context', 'status']) assert.ok(source.includes(`data-zone="${zone}"`));
    assert.equal(f.measurementState.value, 'empty'); await f.select(); assert.equal(f.measurementState.value, 'normal'); assert.match(f.latest().body.sql, /FROM "DeviceID:Original"/u);
    f.props.readOnly = true; f.stage(); f.importRows(1); await f.confirmPendingOperations(); assert.equal(f.measurementState.value, 'readonly'); assert.equal(f.pendingOperations.value.length, 0);
    await f.exportVisiblePoints('json'); assert.equal(f.exports.length, 1); assert.equal(f.calls.filter((call) => call.batch).length, 0);
    f.props.permissionDenied = true; assert.equal(f.measurementState.value, 'permission'); assert.equal(f.columns.value.length, 0); assert.equal(f.pointRows.value.length, 0);
  } finally { f.dispose(); }
});

test('points and monitor exact HTTP/NDJSON authorization codes clear all payloads and latch without body regex', { timeout: 5000 }, async () => {
  const codes = ['forbidden', 'unauthorized', 'http_401', 'http_403'];
  for (let index = 0; index < 8; index += 1) {
    const f = await fixture({ autoReads: false, honorAbort: false }); try {
      await f.select(); f.latest().results([result()]); await flush(); f.stage(); f.importText.value = 'import-secret';
      const run = index < 4 ? f.loadPoints() : f.refreshMonitor(true); f.latest().results([result([], { code: codes[index % 4], message: 'server-secret' })]); await run;
      assert.equal(f.permissionDenied.value, true); assert.equal(f.pointRows.value.length, 0); assert.equal(f.monitorResult.value, null); assert.equal(f.columns.value.length, 0);
      assert.equal(Object.keys(f.pointDraft).length, 0); assert.equal(f.importText.value, ''); assert.equal(f.pendingOperations.value.length, 0); assert.equal(f.approvalPlan.value, null);
      const count = f.calls.length; await f.loadPoints(); await f.refreshMonitor(true); await f.confirmPendingOperations(); assert.equal(f.calls.length, count); assert.ok(!JSON.stringify(f.history).includes('server-secret'));
    } finally { f.dispose(); }
  }
});

test('thrown HTTP401/403 lock while unrelated SQL error text is sanitized without permission false positive', { timeout: 5000 }, async () => {
  for (const status of [401, 403]) { const f = await fixture({ autoReads: false }); try {
    await f.select(); const run = f.loadPoints(); f.latest().reject({ response: { status, data: { message: 'server-secret' } } }); await run; assert.equal(f.permissionDenied.value, true);
  } finally { f.dispose(); } }
  const f = await fixture({ autoReads: false }); try { await f.select(); f.latest().results([result([], { code: 'sql_error', message: 'permission forbidden server-secret' })]); await flush();
    assert.equal(f.permissionDenied.value, false); assert.equal(f.measurementState.value, 'error'); assert.ok(!f.errorMessage.value.includes('server-secret'));
  } finally { f.dispose(); }
});

test('same-token Schema refresh and empty measurement/database/profile/endpoint ABA cannot unlock denial', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false }); try {
    await f.select(); f.latest().error(403); await flush(); const count = f.calls.length; f.auth.state = { token: 'first' };
    f.props.measurement = null; f.props.measurement = measurement(''); f.props.targetDb = ''; f.connections.activeProfileId = ''; f.connections.activeBaseUrl = ''; f.auth.api.defaults.baseURL = '';
    assert.equal(f.measurementState.value, 'permission'); f.props.targetDb = 'North:DB'; f.connections.activeProfileId = 'Profile:Original'; f.connections.activeBaseUrl = 'http://first.invalid'; f.auth.api.defaults.baseURL = 'http://first.invalid'; await f.select();
    await f.loadPoints(); assert.equal(f.permissionDenied.value, true); assert.equal(f.calls.length, count);
  } finally { f.dispose(); }
});

test('point and monitor over-return slice before mapping/export and record actual truncated preview', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false }); try {
    await f.select(); f.pointLimit.value = 500; const readRun = f.loadPoints(); assert.equal(f.latest().body.previewMaxRows, 500); assert.equal(f.latest().body.parameters.limit.integerValue, 500);
    const rows = Array.from({ length: 501 }, (_, index) => [index, 'site', index]); f.latest().results([result(rows)]); await readRun;
    assert.equal(f.pointRows.value.length, 500); assert.equal(f.pointResult.value.end.truncated, true); await f.exportVisiblePoints('json'); assert.equal(JSON.parse(f.exports[0][1]).length, 500);
    f.monitorLimit.value = 5000; const monitor = f.refreshMonitor(true); assert.equal(f.latest().body.previewMaxRows, 500); f.latest().results([result(rows)]); await monitor;
    assert.equal(f.monitorRows.value.length, 500); assert.equal(f.history.at(-1).rowCount, 500); assert.equal(f.history.at(-1).completeness, 'truncated');
  } finally { f.dispose(); }
});

test('same-name database/auth/endpoint/profile/Schema ABA and unmount ignore old point success and monitor denial', { timeout: 5000 }, async () => {
  for (const change of ['database', 'auth', 'endpoint', 'profile', 'schema', 'unmount']) { const f = await fixture({ autoReads: false, honorAbort: false }); try {
    await f.select(); const old = f.latest(); const monitor = f.refreshMonitor(true); const oldMonitor = f.latest();
    if (change === 'database') { f.props.targetDb = 'South:DB'; f.props.targetDb = 'North:DB'; }
    else if (change === 'auth') { f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; }
    else if (change === 'endpoint') { f.auth.api.defaults.baseURL = 'other'; f.auth.api.defaults.baseURL = 'http://first.invalid'; }
    else if (change === 'profile') { f.connections.activeProfileId = 'other'; f.connections.activeProfileId = 'Profile:Original'; }
    else if (change === 'schema') { f.props.measurement.columns[2].dataType = 'STRING'; f.props.measurement.columns[2].dataType = 'DOUBLE'; } else f.dispose();
    old.results([result([[1, 'old-secret', 99]])]); oldMonitor.results([result([], { code: 'forbidden', message: 'old-secret' })]); await monitor; await flush();
    assert.equal(f.pointRows.value.length, 0); assert.equal(f.monitorResult.value, null); assert.equal(f.permissionDenied.value, false); assert.equal(old.signal.aborted, true);
  } finally { f.dispose(); } }
});

test('old point/monitor finally cannot clear newer busy ownership; monitor target/limit own result generation', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false, honorAbort: false }); try {
    await f.select(); const oldRun = f.loadPoints(); const old = f.latest(); const currentRun = f.loadPoints(); const current = f.latest(); old.results([result()]); await oldRun;
    assert.equal(f.loadingPoints.value, true); current.results([result()]); await currentRun;
    const oldMonitorRun = f.refreshMonitor(true); const oldMonitor = f.latest(); f.monitorTarget.value = 'Other:Original'; f.monitorLimit.value = 25;
    const latestRun = f.refreshMonitor(true); const latest = f.latest(); oldMonitor.results([result()]); await oldMonitorRun; assert.equal(f.monitorLoading.value, true);
    assert.match(latest.body.sql, /Other:Original/u); assert.equal(latest.body.previewMaxRows, 25); latest.results([result([[2, 'new', 9]])]); await latestRun; assert.equal(f.monitorRows.value[0].SiteID, 'new');
  } finally { f.dispose(); }
});

test('same-name exact-clone Schema refresh invalidates a late point request and retains the new busy owner', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false, honorAbort: false }); try {
    await f.select(); const oldRun = f.loadPoints(); const old = f.latest(); const count = f.calls.length;
    const schema = { ...f.props.measurement, columns: f.props.measurement.columns.map((column) => ({ ...column })) };
    assert.equal(JSON.stringify(schema), JSON.stringify(f.props.measurement));
    f.props.measurement = schema;
    assert.equal(f.calls.length, count + 1); const current = f.latest(); assert.equal(old.signal.aborted, true);
    old.results([result([[1, 'old-secret', 99]])]); await oldRun; assert.equal(f.loadingPoints.value, true); assert.equal(f.pointRows.value.length, 0);
    current.results([result([[2, 'current-schema', 8]])]); await flush(); assert.equal(f.loadingPoints.value, false); assert.equal(f.pointRows.value[0].SiteID, 'current-schema');
  } finally { f.dispose(); }
});

test('file late arrival never enters a changed/readonly/denied/unmounted identity', { timeout: 5000 }, async () => {
  for (const change of ['identity', 'readonly', 'permission', 'unmount']) { const f = await fixture(); try {
    await f.select(); const text = deferred(); const input = { files: [{ name: 'secret.csv', text: () => text.promise }], value: 'selected' }; const run = f.onFileSelected({ target: input });
    if (change === 'identity') { f.props.targetDb = 'Other'; f.props.targetDb = 'North:DB'; } else if (change === 'readonly') f.props.readOnly = true; else if (change === 'permission') f.props.permissionDenied = true; else f.dispose();
    text.resolve('time,SiteID,ValueID\n1,file-secret,2'); await run; assert.equal(f.importText.value, ''); assert.equal(f.importParsed.value, null); assert.equal(input.value, '');
  } finally { f.dispose(); } }
});

test('approved write consumes once; precise permission terminal clears approval and records original identity', { timeout: 5000 }, async () => {
  const f = await fixture(); try {
    await f.select(); f.stage(); const run = f.confirmPendingOperations(); await f.confirmPendingOperations(); assert.equal(f.calls.filter((call) => call.batch).length, 1);
    f.latest(true).results([result([], { code: 'forbidden', message: 'server-secret' })]); await run; assert.equal(f.permissionDenied.value, true); assert.equal(f.approvalPlan.value, null);
    const entry = f.history.filter((item) => item.kind === 'operation').at(-1); assert.equal(entry.status, 'error'); assert.equal(entry.database, 'North:DB'); assert.equal(entry.target, 'DeviceID:Original'); assert.equal(entry.recordsAffected, 0);
    assert.ok(!JSON.stringify(entry).includes('server-secret')); await f.confirmPendingOperations(); assert.equal(f.calls.filter((call) => call.batch).length, 1);
  } finally { f.dispose(); }
});

test('already-dispatched write context cancellation is unknown at original identity without clearing a new write owner', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false }); try {
    await f.select(); f.stage(); const oldRun = f.confirmPendingOperations(); const old = f.latest(true); f.props.targetDb = 'South:DB'; f.props.targetDb = 'North:DB'; f.stage(); const newRun = f.confirmPendingOperations(); const latest = f.latest(true);
    old.results([terminal()]); await oldRun; assert.equal(f.writeBusy.value, true); const entry = f.history.filter((item) => item.kind === 'operation').at(-1);
    assert.equal(entry.status, 'unknown'); assert.equal(entry.database, 'North:DB'); assert.equal(entry.connectionId, 'Profile:Original'); latest.results([terminal()]); await newRun;
    await f.confirmPendingOperations(); assert.equal(f.calls.filter((call) => call.batch).length, 2);
  } finally { f.dispose(); }
});

test('missing terminal and transport failure stay unknown and consumed', { timeout: 5000 }, async () => {
  for (const failure of ['missing', 'transport']) { const f = await fixture(); try {
    await f.select(); f.stage(); const run = f.confirmPendingOperations(); if (failure === 'missing') f.latest(true).results([{ ...terminal(), end: null, hasColumns: true }]); else f.latest(true).reject(new Error('transport-secret'));
    await run; assert.equal(f.history.filter((item) => item.kind === 'operation').at(-1).status, 'unknown'); assert.equal(f.approvalPlan.value, null); assert.ok(!JSON.stringify(f.history).includes('transport-secret'));
  } finally { f.dispose(); } }
});

test('101 import uses100+1; active user stop preserves100 confirmed points and requires fresh remaining approval', { timeout: 5000 }, async () => {
  const f = await fixture(); try {
    await f.select(); f.importRows(101); const original = f.approvalPlan.value.id; const run = f.confirmPendingOperations(); const first = f.latest(true); assert.equal(first.body.statements.length, 100);
    f.stopImport(); first.results(Array.from({ length: 100 }, terminal)); await run; assert.equal(f.importProgress.value.done, 100); assert.equal(f.pendingOperations.value[0].statements.length, 1); assert.notEqual(f.approvalPlan.value.id, original);
    const resume = f.confirmPendingOperations(); assert.equal(f.latest(true).body.statements.length, 1); f.latest(true).results([terminal()]); await resume; assert.equal(f.calls.filter((call) => call.batch).length, 2);
  } finally { f.dispose(); }
});

test('import refuses1001 and60-second deadline stops next batch without reapproval', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false }); try {
    await f.select(); f.importRows(1001); assert.equal(f.pendingOperations.value.length, 0); f.importRows(101); const run = f.confirmPendingOperations(); const first = f.latest(true);
    await f.advance(60_000); first.results(Array.from({ length: 100 }, terminal)); await run; assert.equal(f.calls.filter((call) => call.batch).length, 1); assert.equal(f.approvalPlan.value, null); assert.equal(f.history.filter((item) => item.kind === 'operation').at(-1).status, 'unknown');
  } finally { f.dispose(); }
});

test('monitor owns finite single-flight12 rounds/60 seconds and leaves no timer', { timeout: 5000 }, async () => {
  const f = await fixture(); try {
    await f.select(); f.activeView.value = 'monitor'; const count = f.calls.length; f.toggleMonitor(); await flush(); for (let index = 0; index < 12; index += 1) await f.advance(2000);
    assert.equal(f.calls.length - count, 12); assert.equal(f.monitorRunning.value, false); assert.equal(f.clock.timers.size, 0);
    f.monitorInterval.value = 30_000; f.toggleMonitor(); await flush(); await f.advance(60_000); assert.equal(f.monitorRunning.value, false); assert.equal(f.clock.timers.size, 0);
  } finally { f.dispose(); }
});

test('production Axios/SQL signals suppress adapter dispatch after same-tick auth/API/profile ABA', { timeout: 5000 }, async () => {
  for (const change of ['auth', 'api', 'profile']) { const f = await fixture({ actualAxios: true }); try {
    await f.select(); f.stage(); const run = f.confirmPendingOperations();
    if (change === 'auth') { f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; }
    else if (change === 'api') { const original = f.auth.api; f.auth.api = f.makeActualClient(); f.auth.api = original; }
    else { f.connections.activeProfileId = 'other'; f.connections.activeProfileId = 'Profile:Original'; }
    await run; await flush(); assert.equal(f.calls.filter((call) => call.batch).length, 0); assert.equal(f.approvalPlan.value, null); assert.equal(f.history.filter((item) => item.kind === 'operation').at(-1).status, 'unknown');
  } finally { f.dispose(); } }
});
