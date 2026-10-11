import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const source = readFileSync(new URL('../src/components/KvKeyspaceWorkbench.vue', import.meta.url), 'utf8');
const { descriptor } = parse(source, { filename: 'KvKeyspaceWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'kv-workbench-migration' });
const apiSource = readFileSync(new URL('../src/api/kv.ts', import.meta.url), 'utf8');
const clientSource = readFileSync(new URL('../src/api/client.ts', import.meta.url), 'utf8');
const uiNames = ['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber',
  'NSelect', 'NSpace', 'NTab', 'NTabs', 'NTag', 'NText'];
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  const entries = Object.entries(exports);
  const deadline = Date.now() + 1000;
  for (let index = 0; index < entries.length && index < 1000 && Date.now() < deadline; index += 1) {
    this.setExport(...entries[index]);
  }
});
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await vue.nextTick(); };
const abortError = () => Object.assign(new Error('Request cancelled'), { code: 'ERR_CANCELED' });

async function fixture({ autoReads = true, honorAbort = true, actualAxios = false } = {}) {
  const calls = [];
  const history = [];
  const notices = [];
  const exports = [];
  const disposers = [];
  const props = vue.reactive({ targetDb: 'alpha', keyspace: 'sessions', keyspaces: ['sessions'], loading: false, readOnly: false, permissionDenied: false });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  // Evaluate the production client module, supplying the Vite build-time base
  // in this Node VM and only replacing Axios' transport with a local adapter.
  const clientModule = new SourceTextModule(stripTypeScriptTypes(clientSource.replace('import.meta.env.BASE_URL', "'http://first.invalid'"), { mode: 'transform' }));
  await clientModule.link((name) => {
    assert.equal(name, 'axios');
    return synthetic({ default: { create(config) {
      return axios.create({ ...config, adapter: async (request) => {
        const action = request.url.split('/').at(-1);
        calls.push({ action, url: request.url, body: typeof request.data === 'string' ? JSON.parse(request.data) : request.data,
          signal: request.signal, baseUrl: request.baseURL, token: request.headers.Authorization });
        const data = action === 'stats' ? { totalKeys: 0, activeKeys: 0, expiredKeys: 0, expiringKeys: 0 }
          : action === 'scan' ? { entries: [], hasMore: false } : action === 'get-many' ? { values: [] } : { applied: true, version: 1 };
        return { config: request, data, status: 200, statusText: 'OK', headers: {} };
      } });
    } } });
  });
  await clientModule.evaluate({ timeout: 3000 });
  const createApiClient = (getToken) => {
    if (actualAxios) return vue.markRaw(clientModule.namespace.createApiClient(getToken));
    const client = {
      defaults: { baseURL: 'http://first.invalid' },
      post(url, body, config = {}) {
        const action = url.split('/').at(-1);
        const call = { action, url, body, signal: config.signal, baseUrl: client.defaults.baseURL, token: getToken() };
        calls.push(call);
        return new Promise((resolve, reject) => {
          const cleanup = () => config.signal?.removeEventListener('abort', abort);
          const abort = () => { if (honorAbort) { cleanup(); reject(abortError()); } };
          call.resolve = (data) => { cleanup(); resolve({ data }); };
          call.reject = (error) => { cleanup(); reject(error); };
          config.signal?.addEventListener('abort', abort, { once: true });
          if (config.signal?.aborted && honorAbort) return abort();
          if (autoReads && action === 'stats') call.resolve({ totalKeys: 0, activeKeys: 0, expiredKeys: 0, expiringKeys: 0 });
          if (autoReads && action === 'scan') call.resolve({ entries: [], nextCursor: null, hasMore: false });
        });
      },
    };
    return vue.markRaw(client);
  };
  const auth = vue.reactive({ state: { token: 'first' }, api: createApiClient(() => auth.state.token) });
  const apiModule = new SourceTextModule(stripTypeScriptTypes(apiSource, { mode: 'transform' }));
  const modules = new Map([
    ['lucide-vue-next', synthetic({ PanelBottom: {} })],
    ['vue', synthetic({ ...vue, onBeforeUnmount: (callback) => disposers.push(callback) })],
    ['naive-ui', synthetic({ ...Object.fromEntries(uiNames.map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'warning', 'error', 'info'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['@/api/kv', apiModule],
    ['@/api/client', actualAxios ? clientModule : synthetic({ createApiClient })],
    ['@/components/WorkbenchHistoryDrawer.vue', synthetic({ default: {} })],
    ['@/components/WorkbenchResultPanel.vue', synthetic({ default: {} })],
    ['@/components/WorkbenchSectionTabs.vue', synthetic({ default: {} })],
    ['@/components/WriteApprovalPanel.vue', synthetic({ default: {} })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })],
    ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (options) => options })],
    ['@/utils/resultExport', synthetic({ downloadText: (fileName, value, contentType) => exports.push({ fileName, value, contentType }), safeFileStem: (value) => value })],
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => {
    assert.ok(modules.has(name), `Unexpected dependency: ${name}`);
    return modules.get(name);
  });
  await module.evaluate({ timeout: 3000 });
  const scope = vue.effectScope();
  const component = scope.run(() => module.namespace.default.setup(props, { expose() {}, emit() {} }));
  const dispose = () => { disposers.forEach((callback) => callback()); scope.stop(); };
  const latest = (action) => calls.filter((call) => call.action === action).at(-1);
  const writes = () => calls.filter((call) => ['set-conditional', 'get-and-set', 'get-and-delete', 'set-many'].includes(call.action));
  const stage = (operation = 'set', key = 'key_a', value = '') => {
    component.singleOperation.value = operation;
    component.editKey.value = key;
    component.editValue.value = value;
    component.stageSetFromEditor();
  };
  const result = () => Object.fromEntries(component.latestResult.value.columns.map((name, index) => [name, component.latestResult.value.rows.at(-1)[index]]));
  await settle();
  return { component, props, auth, connections, calls, history, notices, exports, latest, writes, stage, result, dispose };
}

const entry = (key = 'Original:Key', text = 'visible-value') => ({ key, value: Buffer.from(text).toString('base64'), version: 7, expiresAtUtc: null });
const denial = (status = 403) => ({ response: { status, data: { code: 'server-secret-code', message: 'server-secret-body' } } });

test('KV preserves original database/keyspace/key spelling and actual prefix/cursor request values', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.props.targetDb = 'North:DB';
    f.props.keyspace = 'Space:Original';
    await settle();
    f.component.openPrefix('Prefix:Original:');
    const first = f.latest('scan');
    assert.match(first.url, /North%3ADB\/kv\/Space%3AOriginal\/scan$/u);
    assert.equal(first.body.prefix, 'Prefix:Original:');
    first.resolve({ entries: [entry('Prefix:Original:Key')], nextCursor: 'opaque-server-cursor', hasMore: true });
    await settle();
    const more = f.component.loadMore();
    const next = f.latest('scan');
    assert.equal(next.body.cursor, 'opaque-server-cursor');
    assert.equal(next.body.prefix, 'Prefix:Original:');
    next.resolve({ entries: [entry('Prefix:Original:Second')], hasMore: false });
    await more;
    assert.deepEqual(Array.from(f.component.rows.value, (row) => row.key), ['Prefix:Original:Key', 'Prefix:Original:Second']);
    assert.equal(f.history.at(-1).database, 'North:DB');
    assert.equal(f.history.at(-1).target, 'Space:Original');
    assert.equal(f.history.at(-1).completeness, 'complete');
  } finally { f.dispose(); }
});

test('Scan and Stats 401/403 latch permission and suppress the other late response while clearing all drafts', { timeout: 5000 }, async () => {
  for (const action of ['scan', 'stats']) {
    for (const status of [401, 403]) {
      const f = await fixture({ autoReads: false, honorAbort: false });
      try {
        f.stage('get-and-set', 'draft-secret', 'draft-secret-value');
        f.component.batchKeysText.value = 'delete-secret';
        f.component.batchSetText.value = 'secret=write-draft';
        const scan = f.latest('scan');
        const stats = f.latest('stats');
        f.latest(action).reject(denial(status));
        await settle();
        if (action === 'scan') stats.resolve({ totalKeys: 999, activeKeys: 999 });
        else scan.resolve({ entries: [entry('old-secret', 'old-secret-value')], nextCursor: 'secret-cursor', hasMore: true });
        await settle();
        assert.equal(f.component.kvState.value, 'permission');
        assert.equal(f.component.rows.value.length, 0);
        assert.equal(f.component.stats.value, null);
        assert.equal(f.component.cursor.value, null);
        assert.equal(f.component.hasMore.value, false);
        assert.equal(f.component.latestResult.value, null);
        assert.equal(f.component.editKey.value, '');
        assert.equal(f.component.editValue.value, '');
        assert.equal(f.component.batchSetText.value, '');
        assert.equal(f.component.pendingOperations.value.length, 0);
        assert.ok(!f.component.errorMsg.value.includes('server-secret'));
        const calls = f.calls.length;
        await f.component.refreshAll();
        await f.component.loadKeys(['key'], 'get');
        assert.equal(f.calls.length, calls);
      } finally { f.dispose(); }
    }
  }
});

test('Get denial clears existing value, stats, cursor, result and approval payloads', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('scan').resolve({ entries: [entry()], nextCursor: 'opaque', hasMore: true });
    f.latest('stats').resolve({ totalKeys: 1, activeKeys: 1, expiredKeys: 0, expiringKeys: 0 });
    await settle();
    f.stage('get-and-set', 'draft', 'draft');
    const get = f.component.loadKeys(['Original:Key'], 'get');
    f.latest('get-many').reject(denial());
    await get;
    assert.equal(f.component.permissionDenied.value, true);
    assert.equal(f.component.rows.value.length, 0);
    assert.equal(f.component.stats.value, null);
    assert.equal(f.component.cursor.value, null);
    assert.equal(f.component.latestResult.value, null);
    assert.equal(f.component.previewPlan.value, null);
    assert.ok(!JSON.stringify(f.history).includes('server-secret'));
  } finally { f.dispose(); }
});

test('denial survives same-identity auth refresh and empty keyspace/fallback round trips', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('scan').reject(denial());
    await settle();
    const calls = f.calls.length;
    f.auth.state = { token: 'first' };
    f.props.keyspace = '';
    f.props.keyspaces = [];
    f.props.targetDb = '';
    await settle();
    assert.equal(f.component.kvState.value, 'permission');
    f.props.targetDb = 'alpha';
    f.connections.activeProfileId = '';
    f.connections.activeBaseUrl = '';
    await settle();
    assert.equal(f.component.kvState.value, 'permission');
    f.connections.activeProfileId = 'first';
    f.connections.activeBaseUrl = 'http://first.invalid';
    f.props.keyspaces = ['fallback-is-not-selected'];
    await settle();
    assert.equal(f.component.kvState.value, 'permission');
    f.props.keyspaces = ['sessions'];
    f.props.keyspace = 'sessions';
    await settle();
    await f.component.refreshAll();
    assert.equal(f.component.permissionDenied.value, true);
    assert.equal(f.calls.length, calls);
    f.props.targetDb = 'new-database';
    await settle();
    assert.equal(f.component.permissionDenied.value, false);
    assert.ok(f.calls.length > calls);
  } finally { f.dispose(); }
});

test('readonly retains Scan/Get/export and blocks every write staging/import/confirmation entry', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.props.readOnly = true;
    await settle();
    assert.equal(f.component.kvState.value, 'readonly');
    f.component.currentPrefix.value = 'prefix:';
    f.component.rows.value = [f.component.mapEntry(entry())];
    f.component.selectEntry('Original:Key');
    f.component.checkedRowKeys.value = ['Original:Key'];
    f.component.exportRoundTrip();
    assert.equal(f.exports.length, 1);
    f.component.batchKeysText.value = 'Original:Key';
    f.component.batchSetText.value = 'write=value';
    f.stage('set', 'write', 'value');
    f.component.stageBatchSet();
    f.component.stageSetMany([{ key: 'write', value: 'dmFsdWU=' }], null, 'write', 'value');
    f.component.stageRoundTripImport(JSON.stringify({ key: 'write', valueBase64: 'dmFsdWU=' }), 'file');
    f.component.stageExpireSelected();
    f.component.stagePersistSelected();
    f.component.stageRemoveSelected();
    f.component.stageRemoveExplicitKeys();
    f.component.stageRemoveKeys(['Original:Key']);
    f.component.stagePrefixDelete();
    f.component.stageCleanExpired();
    await f.component.confirmPendingOperations();
    assert.equal(f.component.pendingOperations.value.length, 0);
    assert.equal(f.writes().length, 0);
    const get = f.component.loadKeys(['Original:Key'], 'readonly get');
    f.latest('get-many').resolve({ values: [{ ...entry(), found: true }] });
    await get;
    assert.equal(f.component.latestResult.value.end.rowCount, 1);
    await f.component.loadEntries(true);
  } finally { f.dispose(); }
});

test('scan per-page clamps and accumulated 1000 preview discards an over-return cursor before mapping', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.component.scanLimit.value = 600;
    const first = f.component.loadEntries(true);
    f.latest('scan').resolve({ entries: Array.from({ length: 600 }, (_, i) => entry(`key-${i}`)), nextCursor: 'cursor-one', hasMore: true });
    await first;
    assert.equal(f.component.rows.value.length, 600);
    const more = f.component.loadMore();
    assert.equal(f.latest('scan').body.limit, 400);
    f.latest('scan').resolve({ entries: [...Array.from({ length: 400 }, (_, i) => entry(`key-${600 + i}`)), ...Array.from({ length: 100 }, () => ({ value: 'invalid-discarded-tail' }))], nextCursor: 'would-skip-discarded', hasMore: true });
    await more;
    assert.equal(f.component.rows.value.length, 1000);
    assert.equal(f.component.latestResult.value.rows.length, 1000);
    assert.equal(f.component.latestResult.value.end.truncated, true);
    assert.equal(f.component.cursor.value, null);
    assert.equal(f.component.hasMore.value, false);
    assert.equal(f.history.at(-1).rowCount, 1000);
    assert.equal(f.history.at(-1).completeness, 'truncated');
    const calls = f.calls.length;
    await f.component.loadMore();
    assert.equal(f.calls.length, calls);
    f.component.scanLimit.value = 9999;
    const refresh = f.component.loadEntries(true);
    assert.equal(f.latest('scan').body.limit, 1000);
    f.latest('scan').resolve({ entries: [], hasMore: false });
    await refresh;
    f.component.scanLimit.value = -2;
    const minimal = f.component.loadEntries(true);
    assert.equal(f.latest('scan').body.limit, 1);
    f.latest('scan').resolve({ entries: [], hasMore: false });
    await minimal;
  } finally { f.dispose(); }
});

test('new prefix/reset failure or scan-budget change cannot reuse a cursor from an older window', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('scan').resolve({ entries: [entry()], nextCursor: 'old-window', hasMore: true });
    await settle();
    f.component.openPrefix('new:');
    assert.equal(f.component.cursor.value, null);
    assert.equal(f.component.hasMore.value, false);
    f.latest('scan').reject(new Error('server-secret failure'));
    await settle();
    const count = f.calls.length;
    await f.component.loadMore();
    assert.equal(f.calls.length, count);
    assert.ok(!f.component.errorMsg.value.includes('server-secret'));
    f.component.cursor.value = 'old-budget';
    f.component.hasMore.value = true;
    f.component.scanLimit.value = 50;
    assert.equal(f.component.cursor.value, null);
    assert.equal(f.component.hasMore.value, false);
  } finally { f.dispose(); }
});

test('Get sends and formats at most 1000 requested values and records bounded result completeness', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const get = f.component.loadKeys(Array.from({ length: 1002 }, (_, i) => `key-${i}`), 'batch get');
    assert.equal(f.latest('get-many').body.keys.length, 1000);
    f.latest('get-many').resolve({ values: [...Array.from({ length: 1000 }, (_, i) => ({ ...entry(`key-${i}`), found: true })), null, null] });
    await get;
    assert.equal(f.component.latestResult.value.rows.length, 1000);
    assert.equal(f.component.latestResult.value.end.truncated, true);
    assert.equal(f.history.at(-1).rowCount, 1000);
    assert.equal(f.history.at(-1).completeness, 'truncated');
  } finally { f.dispose(); }
});

test('Inspector formats only the first 4096 raw bytes in all four modes and exports complete round-trip raw', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    const raw = entry('Long:Original', 'a'.repeat(4096) + 'tail-secret-not-in-preview');
    f.latest('scan').resolve({ entries: [raw], hasMore: false });
    await settle();
    assert.equal(f.component.selectedEntry.value.byteLength, 4122);
    assert.equal(f.component.editValue.value, '');
    assert.equal(f.component.editKey.value, '');
    for (const mode of ['text', 'json', 'hex', 'base64']) {
      f.component.valueView.value = mode;
      const displayed = f.component.selectedValueText.value;
      assert.ok(!displayed.includes('tail-secret'));
      if (mode === 'text' || mode === 'json') assert.equal(displayed.length, 4096);
      if (mode === 'hex') assert.equal(displayed.replaceAll(' ', '').length, 8192);
      if (mode === 'base64') assert.equal(Buffer.from(displayed, 'base64').length, 4096);
      f.component.editMode.value = mode;
      await settle();
      assert.equal(f.component.editValue.value, '');
    }
    assert.equal(f.component.kvState.value, 'longContent');
    f.component.exportRoundTrip();
    assert.equal(JSON.parse(f.exports[0].value.trim()).valueBase64, raw.value);
  } finally { f.dispose(); }
});

test('short values retain full edit and exact round-trip base64 behavior', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    const raw = entry('Short:Original', '{"value":7}');
    f.latest('scan').resolve({ entries: [raw], hasMore: false });
    await settle();
    assert.equal(f.component.editKey.value, raw.key);
    assert.equal(JSON.parse(f.component.editValue.value).value, 7);
    f.component.exportRoundTrip();
    assert.equal(JSON.parse(f.exports[0].value.trim()).valueBase64, raw.value);
  } finally { f.dispose(); }
});

test('late Scan/Get/Stats successes and failures are isolated across database/auth/profile ABA and unmount', { timeout: 5000 }, async () => {
  for (const change of ['database', 'auth', 'profile', 'unmount']) {
    const f = await fixture({ autoReads: false, honorAbort: false });
    try {
      const scan = f.latest('scan');
      const stats = f.latest('stats');
      const getRun = f.component.loadKeys(['old'], 'get');
      const get = f.latest('get-many');
      if (change === 'database') { f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; }
      else if (change === 'auth') { f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; }
      else if (change === 'profile') { f.connections.activeBaseUrl = 'http://other.invalid'; f.connections.activeBaseUrl = 'http://first.invalid'; }
      else f.dispose();
      scan.resolve({ entries: [entry('old-secret')], hasMore: true });
      stats.resolve({ totalKeys: 999, activeKeys: 999 });
      get.reject(denial());
      await getRun;
      await settle();
      assert.equal(f.component.rows.value.length, 0);
      assert.equal(f.component.stats.value, null);
      assert.equal(f.component.permissionDenied.value, false);
      assert.equal(f.component.latestResult.value, null);
      assert.equal(scan.signal.aborted, true);
    } finally { f.dispose(); }
  }
});

test('a newer Get owns its result when an old request ignores Abort', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    const oldRun = f.component.loadKeys(['old'], 'get');
    const old = f.latest('get-many');
    const latestRun = f.component.loadKeys(['current'], 'get');
    f.latest('get-many').resolve({ values: [{ ...entry('current', 'current-value'), found: true }] });
    await latestRun;
    old.resolve({ values: [{ ...entry('old', 'old-secret'), found: true }] });
    await oldRun;
    assert.equal(f.component.latestResult.value.rows[0][0], 'current');
    assert.ok(!JSON.stringify(f.component.latestResult.value).includes('old-secret'));
  } finally { f.dispose(); }
});

test('write 403 locks the page, discards previous-value payloads and keeps only sanitized known-count history', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage('get-and-set', 'first', 'write');
    f.stage('get-and-delete', 'second');
    const run = f.component.confirmPendingOperations();
    f.latest('get-and-set').resolve({ previous: { found: true, value: Buffer.from('previous-secret').toString('base64'), version: 4 }, mutationVersion: 5 });
    await settle();
    f.latest('get-and-delete').reject(denial());
    await run;
    assert.equal(f.component.kvState.value, 'permission');
    assert.equal(f.component.latestResult.value, null);
    assert.equal(f.component.previewPlan.value, null);
    assert.equal(f.history[0].status, 'error');
    assert.equal(f.history[0].recordsAffected, 1);
    assert.ok(!JSON.stringify(f.history).includes('server-secret'));
    assert.ok(!JSON.stringify(f.history).includes('previous-secret'));
    await f.component.confirmPendingOperations();
    assert.equal(f.writes().length, 2);
  } finally { f.dispose(); }
});

test('transport failure and missing atomic terminal response remain unknown without restoring approval', { timeout: 5000 }, async () => {
  for (const missing of [false, true]) {
    const f = await fixture();
    try {
      f.stage('get-and-delete', 'first');
      const run = f.component.confirmPendingOperations();
      if (missing) f.latest('get-and-delete').resolve(null);
      else f.latest('get-and-delete').reject(new Error('transport-secret'));
      await run;
      assert.equal(f.history[0].status, 'unknown');
      assert.equal(f.result().state, 'unknown');
      assert.equal(f.component.pendingOperations.value.length, 0);
      assert.ok(!JSON.stringify(f.history).includes('transport-secret'));
      await f.component.confirmPendingOperations();
      assert.equal(f.writes().length, 1);
    } finally { f.dispose(); }
  }
});

test('runtime KV exposes normal/empty/error/readonly/permission/longContent without static state simulation', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    assert.equal(f.component.kvState.value, 'normal');
    f.latest('scan').resolve({ entries: [], hasMore: false });
    await settle();
    assert.equal(f.component.kvState.value, 'empty');
    f.component.errorMsg.value = 'safe failure';
    assert.equal(f.component.kvState.value, 'error');
    f.props.readOnly = true;
    assert.equal(f.component.kvState.value, 'readonly');
    f.props.permissionDenied = true;
    assert.equal(f.component.kvState.value, 'permission');
    f.props.permissionDenied = false;
    f.props.readOnly = false;
    f.component.previewTruncated.value = true;
    assert.equal(f.component.kvState.value, 'longContent');
  } finally { f.dispose(); }
});

test('production Axios client and KV Get helper cancel before adapter dispatch on synchronous database ABA', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    assert.ok(f.calls.some((call) => call.action === 'scan' && call.token === 'Bearer first'));
    const running = f.component.loadKeys(['Original:Key'], 'get');
    f.props.targetDb = 'beta';
    f.props.targetDb = 'alpha';
    await running;
    await settle();
    assert.equal(f.calls.filter((call) => call.action === 'get-many').length, 0);
    assert.ok(!f.history.some((item) => item.action === 'get'));
  } finally { f.dispose(); }
});

test('production Axios request interceptor cannot dispatch an approved KV write after synchronous authentication ABA', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    f.stage('set', 'Original:Key', 'original draft');
    const running = f.component.confirmPendingOperations();
    f.auth.state = { token: 'second' };
    f.auth.state = { token: 'first' };
    await running;
    await settle();
    assert.equal(f.writes().length, 0);
    assert.equal(f.component.pendingOperations.value.length, 0);
    assert.equal(f.history[0].status, 'unknown');
    assert.equal(f.history[0].database, 'alpha');
    assert.equal(f.history[0].target, 'sessions');
    await f.component.confirmPendingOperations();
    assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});