import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const source = readFileSync(new URL('../src/components/KvKeyspaceWorkbench.vue', import.meta.url), 'utf8');
const { descriptor } = parse(source, { filename: 'KvKeyspaceWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'kv-workflow-regression' });
const apiSource = readFileSync(new URL('../src/api/kv.ts', import.meta.url), 'utf8');
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

async function fixture({ autoReads = true, honorAbort = true } = {}) {
  const calls = [];
  const history = [];
  const notices = [];
  const disposers = [];
  const props = vue.reactive({ targetDb: 'alpha', keyspace: 'sessions', keyspaces: ['sessions'], loading: false });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  const createApiClient = (getToken) => {
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
    ['@/api/client', synthetic({ createApiClient })],
    ['@/components/WorkbenchHistoryDrawer.vue', synthetic({ default: {} })],
    ['@/components/WorkbenchResultPanel.vue', synthetic({ default: {} })],
    ['@/components/WorkbenchSectionTabs.vue', synthetic({ default: {} })],
    ['@/components/WriteApprovalPanel.vue', synthetic({ default: {} })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })],
    ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (options) => options })],
    ['@/utils/resultExport', synthetic({ downloadText() {}, safeFileStem: (value) => value })],
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
  return { component, props, auth, connections, calls, history, notices, latest, writes, stage, result, dispose };
}

test('KV NX is one conditional request and a rejected condition is a successful no-op', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.setCondition.value = 1;
    f.stage('set', 'empty', '');
    const running = f.component.confirmPendingOperations();
    const write = f.latest('set-conditional');
    assert.deepEqual(JSON.parse(JSON.stringify(write.body)), { key: 'empty', value: '', expiresAtUtc: null, condition: 1 });
    assert.ok(write.signal instanceof AbortSignal);
    write.resolve({ applied: false });
    await running;
    assert.equal(f.result().applied, false);
    assert.equal(f.result().affected, 0);
    assert.equal(f.result().succeeded, true);
    assert.equal(f.result().state, 'not-applied');
    assert.equal(f.history[0].status, 'success');
    assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('KV exchange preserves a found empty previous value and both versions in its result', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.setExpiryMode.value = 'seconds';
    f.component.setTtlSeconds.value = 60;
    f.stage('get-and-set', 'empty', 'new');
    const running = f.component.confirmPendingOperations();
    const write = f.latest('get-and-set');
    assert.equal(write.body.value, 'bmV3');
    assert.ok(Date.parse(write.body.expiresAtUtc) > Date.now());
    write.resolve({ previous: { found: true, value: '', version: 4, expiresAtUtc: null }, mutationVersion: 5 });
    await running;
    assert.equal(f.result().previousFound, true);
    assert.equal(f.result().previousValueBase64, '');
    assert.equal(f.result().previousVersion, 4);
    assert.equal(f.result().mutationVersion, 5);
    assert.equal(f.history[0].recordsAffected, 1);
    assert.equal(f.history[0].command.includes('bmV3'), false);
    assert.match(f.history[0].summary, /previousVersion=4, mutationVersion=5/);
  } finally { f.dispose(); }
});

test('KV conditional versions prefer exact decimal text above the JavaScript safe integer limit', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const exact = '9007199254740993';
    f.stage('set', 'versioned', 'new');
    const running = f.component.confirmPendingOperations();
    f.latest('set-conditional').resolve({ applied: true, version: Number(exact), versionText: exact });
    await running;
    assert.equal(f.result().version, exact);
    assert.match(f.history[0].summary, /version=9007199254740993/);
    assert.equal(f.history[0].summary.includes(String(Number(exact))), false);
  } finally { f.dispose(); }
});

test('KV exchange versions and history retain both decimal string companions without rounding', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const previous = '9007199254740993';
    const mutation = '9223372036854775807';
    f.stage('get-and-set', 'versioned', 'new');
    const running = f.component.confirmPendingOperations();
    f.latest('get-and-set').resolve({
      previous: { found: true, value: '', version: Number(previous) },
      mutationVersion: Number(mutation), previousVersionText: previous, mutationVersionText: mutation,
    });
    await running;
    assert.equal(f.result().previousVersion, previous);
    assert.equal(f.result().mutationVersion, mutation);
    assert.match(f.history[0].summary, /previousVersion=9007199254740993, mutationVersion=9223372036854775807/);
    assert.equal(f.history[0].summary.includes(String(Number(mutation))), false);
  } finally { f.dispose(); }
});

test('Older KV responses mark unsafe numeric versions unavailable while preserving safe numeric fallback', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const unsafe = Number('9007199254740993');
    f.stage('get-and-delete', 'versioned');
    const running = f.component.confirmPendingOperations();
    f.latest('get-and-delete').resolve({ previous: { found: true, value: '', version: unsafe }, mutationVersion: 42 });
    await running;
    assert.equal(f.result().previousVersion, 'unavailable (unsafe numeric version)');
    assert.equal(f.result().mutationVersion, 42);
    assert.match(f.history[0].summary, /previousVersion=unavailable \(unsafe numeric version\), mutationVersion=42/);
    assert.equal(f.history[0].summary.includes(String(unsafe)), false);
  } finally { f.dispose(); }
});

test('KV get-and-delete of a missing key exposes absence without inventing a mutation version', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage('get-and-delete');
    assert.equal(f.component.pendingOperations.value[0].severity, 'danger');
    const running = f.component.confirmPendingOperations();
    f.latest('get-and-delete').resolve({ previous: { found: false } });
    await running;
    assert.equal(f.result().previousFound, false);
    assert.equal(f.result().previousValueBase64, null);
    assert.equal(f.result().mutationVersion, null);
    assert.equal(f.result().affected, 0);
  } finally { f.dispose(); }
});

test('KV approvals expire on target, connection, token and mutable API URL changes', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage();
    f.props.keyspace = 'other';
    await f.component.confirmPendingOperations();
    f.stage();
    f.connections.activeProfileId = 'second';
    await f.component.confirmPendingOperations();
    f.stage();
    f.auth.state.token = 'second';
    await f.component.confirmPendingOperations();
    f.stage();
    f.auth.api.defaults.baseURL = 'http://other.invalid';
    await f.component.confirmPendingOperations();
    assert.equal(f.writes().length, 0);
    assert.equal(f.component.pendingOperations.value.length, 0);
  } finally { f.dispose(); }
});

test('A dispatched old-target KV write retains its identity and stops later writes after a switch', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.stage('get-and-set', 'first', 'one');
    f.stage('get-and-delete', 'second');
    const operation = f.component.pendingOperations.value[0];
    const running = f.component.confirmPendingOperations();
    const write = f.latest('get-and-set');
    f.props.targetDb = 'beta';
    f.auth.state.token = 'second';
    f.auth.api.defaults.baseURL = 'http://second.invalid';
    await settle();
    assert.equal(write.signal.aborted, true);
    assert.equal(operation.context.api.defaults.baseURL, 'http://first.invalid');
    assert.equal(write.token, 'first');
    write.resolve({ previous: { found: false }, mutationVersion: 1 });
    await running;
    assert.equal(f.writes().length, 1);
    assert.equal(f.history[0].database, 'alpha');
    assert.equal(f.history[0].target, 'sessions');
    assert.equal(f.history[0].recordsAffected, 1);
    assert.equal(f.history[0].status, 'cancelled');
    assert.equal(f.notices.length, 0);
    assert.equal(f.component.latestCommand.value.includes('GET-AND-SET'), false);
  } finally { f.dispose(); }
});

test('KV cancellation marks an in-flight write unknown and never replays the dispatched batch', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage('get-and-delete', 'first');
    f.stage('get-and-set', 'second');
    const running = f.component.confirmPendingOperations();
    assert.ok(f.component.previewPlan.value);
    f.component.abortPendingOperations();
    await running;
    assert.equal(f.result().state, 'unknown');
    assert.equal(f.history[0].status, 'cancelled');
    assert.equal(f.component.pendingOperations.value.length, 0);
    await f.component.confirmPendingOperations();
    assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('KV partial failure retains completed outcomes and stable rejection codes without replay', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage('get-and-set', 'first');
    f.stage('get-and-delete', 'second');
    f.stage('set', 'third');
    const running = f.component.confirmPendingOperations();
    f.latest('get-and-set').resolve({ previous: { found: true, value: 'b2xk', version: 6 }, mutationVersion: 7 });
    await settle();
    f.latest('get-and-delete').reject({ response: { status: 403, data: { code: 'forbidden', message: 'Access denied' } } });
    await running;
    assert.equal(f.component.latestResult.value.rows.length, 2);
    assert.equal(f.component.latestResult.value.end.recordsAffected, 1);
    assert.equal(f.result().state, 'failed');
    assert.equal(f.result().errorCode, 'forbidden');
    assert.equal(f.history[0].recordsAffected, 1);
    assert.equal(f.history[0].status, 'error');
    await f.component.confirmPendingOperations();
    assert.equal(f.writes().length, 2);
  } finally { f.dispose(); }
});

test('KV refresh preserves the atomic editor draft and a target change clears it', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('scan').resolve({ entries: [{ key: 'existing', value: 'b2xk', version: 1 }], hasMore: false });
    await settle();
    f.component.activeView.value = 'batch';
    f.stage('get-and-set', 'draft', 'pending');
    const refresh = f.component.loadEntries(true, false);
    f.latest('scan').resolve({ entries: [{ key: 'existing', value: 'bmV3', version: 2 }], hasMore: false });
    await refresh;
    await settle();
    assert.equal(f.component.editKey.value, 'draft');
    assert.equal(f.component.editValue.value, 'pending');
    f.props.targetDb = 'beta';
    await settle();
    assert.equal(f.component.editKey.value, '');
    assert.equal(f.component.editValue.value, '');
  } finally { f.dispose(); }
});

test('KV scan responses cannot overwrite a newer prefix, target or loading state', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false, honorAbort: false });
  try {
    const oldScan = f.latest('scan');
    const oldStats = f.latest('stats');
    f.props.keyspace = 'other';
    await settle();
    const otherScan = f.latest('scan');
    f.component.openPrefix('new:');
    const newest = f.latest('scan');
    newest.resolve({ entries: [{ key: 'new:empty', value: '', version: 9 }], nextCursor: null, hasMore: false });
    await settle();
    oldScan.resolve({ entries: [{ key: 'old', value: '', version: 1 }], hasMore: true });
    otherScan.reject(new Error('obsolete failure'));
    oldStats.resolve({ activeKeys: 999 });
    await settle();
    assert.deepEqual(Array.from(f.component.rows.value, (row) => row.key), ['new:empty']);
    assert.equal(f.component.loadingScan.value, false);
    assert.equal(f.component.errorMsg.value, '');
    assert.equal(f.component.stats.value, null);
  } finally { f.dispose(); }
});
