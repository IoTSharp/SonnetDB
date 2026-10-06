import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const sourcePath = new URL('../src/components/VectorSearchWorkbench.vue', import.meta.url);
const source = readFileSync(sourcePath, 'utf8');
const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/u)[1];
const apiSource = readFileSync(new URL('../src/api/vector.ts', import.meta.url), 'utf8');
const { computed, h, markRaw, nextTick, reactive, ref, watch } = vue;

function synthetic(exports) {
  return new SyntheticModule(Object.keys(exports), function () {
    for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
  });
}

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((done, failed) => { resolve = done; reject = failed; });
  return { promise, resolve, reject };
}

const index = (measurement = 'Vectors:Original', column = 'Embedding:Original') => ({
  measurement, column, kind: 'hnsw', dimension: 3, metric: 'cosine', params: [{ key: 'm', value: '16' }], rowCount: 20,
});
const measurement = () => ({ name: 'Vectors:Original', columns: [
  { name: 'Time', role: 'TIME', dataType: 'int64' },
  { name: 'Embedding:Original', role: 'FIELD', dataType: 'vector', vectorDimension: 3, vectorIndex: { kind: 'hnsw', options: [] } },
] });
const hit = (timestampUtc = 1, text = 'visible-field') => ({
  timestampUtc, distance: 0.25, tags: [{ key: 'Source', value: 'visible-tag' }], fields: [{ key: 'Text', value: text }],
});

async function loadWorkbench(post, options = {}) {
  const props = reactive({ targetDb: 'North:DB', index: index(), indexes: [index()], measurement: measurement(), loading: false, readOnly: false, permissionDenied: false });
  const auth = reactive({ state: { token: 'token-a', username: 'reader' }, api: markRaw({ post, defaults: { baseURL: '/gateway' } }) });
  const connection = reactive({ activeProfileId: 'profile-a', activeProfile: { id: 'profile-a', name: 'Connection A', baseUrl: '/gateway' } });
  const historyEntries = [];
  const cleanups = [];
  const calls = [];
  const client = (getToken) => {
    const api = {
      defaults: { baseURL: '/gateway' },
      async post(path, request) {
        const config = { signal: api.defaults.signal };
        calls.push({ path, request: JSON.parse(JSON.stringify(request)), token: getToken(), endpoint: api.defaults.baseURL, signal: config.signal });
        return post(path, request, config);
      },
    };
    return api;
  };
  const placeholder = (name) => vue.defineComponent({ name, setup(_, { slots }) { return () => h('section', slots.default?.()); } });
  const dependencies = {
    vue: synthetic(options.mount ? { ...vue } : { ...vue, onBeforeUnmount: (cleanup) => cleanups.push(cleanup) }),
    'naive-ui': synthetic({
      ...Object.fromEntries(['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NSelect', 'NSpace', 'NTab', 'NTabs', 'NTag', 'NText']
        .map((name) => [name, placeholder(name)])),
      useMessage: () => ({ success() {}, warning() {} }),
    }),
    '@/api/client': synthetic({ createApiClient: options.createApiClient ?? client }),
    '@/api/management': synthetic({}),
    '@/api/schema': synthetic({}),
    '@/api/sql': synthetic({ quote: (value) => `'${value.replaceAll("'", "''")}'` }),
    '@/stores/auth': synthetic({ useAuthStore: () => auth }),
    '@/stores/connections': synthetic({ useConnectionsStore: () => connection }),
    '@/stores/workbenchHistory': synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => historyEntries.push(entry) }) }),
    '@/utils/sqlWorkbench': synthetic({ formatSqlIdentifier: (value) => `"${value.replaceAll('"', '""')}"` }),
  };
  for (const name of ['WorkbenchHistoryDrawer', 'MeasurementWorkbench', 'WorkbenchResultPanel', 'WorkbenchSectionTabs']) {
    dependencies[`@/components/${name}.vue`] = synthetic({ default: name === 'MeasurementWorkbench' && options.child ? options.child : placeholder(name) });
  }
  const vectorModule = new SourceTextModule(stripTypeScriptTypes(apiSource, { mode: 'transform' }));
  await vectorModule.link((name) => { throw new Error(`Unexpected vector API runtime import ${name}`); });
  dependencies['@/api/vector'] = vectorModule;
  dependencies.fixture = synthetic({ props });
  const exports = `export { runSearch, parseRawVector, embedTextToVector, hits, hitsTruncated, latestResult, latestCommand,
    permissionLocked, permissionDenied, vectorState, rawVectorText, embedText, filterText, queryVector, queryMode, metric,
    topK, errorMsg, contextSnapshot, captureContext, canSearch, hitRows, dataWorkbenchKey,
    activeView, dataGeneration, searching, onMeasurementPermissionRejected };`;
  const moduleSource = options.mount
    ? compileScript(parse(source).descriptor, { id: 'vector-fixture', inlineTemplate: true }).content
    : `import { props as fixtureProps } from 'fixture';
      const defineProps = () => fixtureProps;
      const withDefaults = (value) => value;
      const defineEmits = () => () => {};
      ${script}\n${exports}`;
  const module = new SourceTextModule(stripTypeScriptTypes(moduleSource, { mode: 'transform' }), { identifier: sourcePath.href });
  await module.link((specifier) => {
    assert.ok(dependencies[specifier], `Unexpected dependency: ${specifier}`);
    return dependencies[specifier];
  });
  await module.evaluate({ timeout: 3000 });
  return { ...module.namespace, searchPreview: vectorModule.namespace.searchVectorPreview, props, auth, connection, calls, historyEntries, cleanup: () => cleanups.forEach((item) => item()) };
}

test('Vector keeps original names and freezes query, metric, filter, Top-K and Token for its request/history', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    workbench.rawVectorText.value = '[0.1, 0.2, 0.3]';
    workbench.metric.value = 'l2';
    workbench.filterText.value = "Source = 'original'";
    workbench.topK.value = 3;
    const run = workbench.runSearch();
    workbench.rawVectorText.value = '[3, 2, 1]';
    workbench.metric.value = 'inner_product';
    workbench.filterText.value = 'changed filter';
    workbench.topK.value = 100;
    workbench.connection.activeProfile.name = 'Renamed connection';
    pending.resolve({ data: { hits: [hit(1), hit(2), hit(3), hit(4)] } });
    await run;
    assert.deepEqual(workbench.calls[0].request, { measurement: 'Vectors:Original', column: 'Embedding:Original', query: [0.1, 0.2, 0.3], topK: 3, metric: 'l2', filter: "Source = 'original'" });
    assert.equal(workbench.calls[0].path, '/v1/db/North%3ADB/vector/search-preview');
    assert.equal(workbench.calls[0].token, 'token-a');
    assert.equal(workbench.calls[0].endpoint, '/gateway');
    assert.equal(workbench.latestResult.value.end.rowCount, 3);
    assert.equal(workbench.latestResult.value.end.truncated, true);
    assert.equal(workbench.historyEntries[0].database, 'North:DB');
    assert.equal(workbench.historyEntries[0].connectionName, 'Connection A');
    assert.equal(workbench.historyEntries[0].target, 'Vectors:Original.Embedding:Original');
    assert.equal(workbench.historyEntries[0].completeness, 'truncated');
    assert.match(workbench.latestCommand.value, /"Vectors:Original", "Embedding:Original"/u);
    assert.ok(!workbench.latestCommand.value.includes('changed filter'));
  } finally { workbench.cleanup(); }
});

test('Vector preview clamps Top-K 1..100 and slices before formatting even a malformed discarded tail', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [...Array.from({ length: 100 }, (_, i) => hit(i)), { fields: 'private-invalid-tail' }] } }));
  try {
    workbench.topK.value = 999;
    await workbench.runSearch();
    assert.equal(workbench.calls[0].request.topK, 100);
    assert.equal(workbench.hits.value.length, 100);
    assert.equal(workbench.hitRows.value.length, 100);
    assert.equal(workbench.latestResult.value.rows.length, 100);
    assert.equal(workbench.hitsTruncated.value, true);
    assert.equal(workbench.vectorState.value, 'longContent');
    workbench.topK.value = -10;
    await workbench.runSearch();
    assert.equal(workbench.calls[1].request.topK, 1);
    assert.equal(workbench.hits.value.length, 1);
  } finally { workbench.cleanup(); }
});

test('raw Vector rejects non-number JSON, non-finite values and mismatched or unavailable dimensions before dispatch', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }));
  try {
    for (const text of ['[null, false, "1"]', '[1, 2]', '[1, 2, 1e999]', 'NaN, 2, 3', '[]', '[{"secret":"input-secret"}]']) {
      workbench.rawVectorText.value = text;
      await workbench.runSearch();
      assert.equal(workbench.canSearch.value, false);
      assert.equal(workbench.queryVector.value.length, 0);
      assert.ok(!workbench.errorMsg.value.includes('input-secret'));
    }
    workbench.props.index.dimension = null;
    workbench.rawVectorText.value = '[1,2,3]';
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 0);
    workbench.props.index.dimension = 3;
    workbench.rawVectorText.value = '1, 2, 3';
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 1);
  } finally { workbench.cleanup(); }
});

test('Search parses the current raw editor rather than dispatching an older parsed vector', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }));
  try {
    workbench.parseRawVector();
    workbench.rawVectorText.value = '[8,9,10]';
    await workbench.runSearch();
    assert.deepEqual(workbench.calls[0].request.query, [8, 9, 10]);
    workbench.rawVectorText.value = '[8,9]';
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 1);
  } finally { workbench.cleanup(); }
});

test('late Vector responses cannot cross a same-name database/resource ABA or unmount', { timeout: 5000 }, async () => {
  for (const change of ['database', 'resource', 'unmount']) {
    const pending = deferred();
    const workbench = await loadWorkbench(() => pending.promise);
    try {
      const run = workbench.runSearch();
      if (change === 'database') {
        workbench.props.targetDb = 'South:DB';
        workbench.props.targetDb = 'North:DB';
      } else if (change === 'resource') {
        workbench.props.index = index('Other:Measurement');
        workbench.props.index = index();
      } else workbench.cleanup();
      pending.resolve({ data: { hits: [hit(1, 'old-secret')] } });
      await run;
      assert.equal(workbench.hits.value.length, 0);
      assert.equal(workbench.latestResult.value, null);
      assert.equal(workbench.historyEntries.length, 0);
      assert.equal(workbench.calls[0].signal.aborted, true);
    } finally { workbench.cleanup(); }
  }
});

test('a newer Vector search owns loading/results when an older success or denial ignores abort', { timeout: 5000 }, async () => {
  for (const deny of [false, true]) {
    const pending = deferred();
    let searches = 0;
    const workbench = await loadWorkbench(async () => ++searches === 1 ? pending.promise : { data: { hits: [hit(2, 'current-field')] } });
    try {
      const oldRun = workbench.runSearch();
      await workbench.runSearch();
      if (deny) pending.reject({ response: { status: 403, data: { message: 'old-secret-denial' } } });
      else pending.resolve({ data: { hits: [hit(1, 'old-secret')] } });
      await oldRun;
      assert.equal(workbench.hits.value[0].timestampUtc, 2);
      assert.equal(workbench.permissionDenied.value, false);
      assert.equal(workbench.historyEntries.length, 1);
      assert.ok(!JSON.stringify(workbench.latestResult.value).includes('old-secret'));
    } finally { workbench.cleanup(); }
  }
});

test('401/403 removes hits, tags, fields, parsed vector and results while preserving user raw/text/filter input', { timeout: 5000 }, async () => {
  for (const status of [401, 403]) {
    let deny = false;
    const workbench = await loadWorkbench(async () => {
      if (deny) throw { response: { status, data: { message: 'server-secret-denial' } } };
      return { data: { hits: [hit()] } };
    });
    try {
      await workbench.runSearch();
      workbench.embedText.value = 'keep text draft';
      workbench.filterText.value = 'keep filter draft';
      const raw = workbench.rawVectorText.value;
      deny = true;
      await workbench.runSearch();
      assert.equal(workbench.vectorState.value, 'permission');
      assert.equal(workbench.hits.value.length, 0);
      assert.equal(workbench.hitRows.value.length, 0);
      assert.equal(workbench.queryVector.value.length, 0);
      assert.equal(workbench.latestResult.value, null);
      assert.equal(workbench.latestCommand.value, '');
      assert.equal(workbench.rawVectorText.value, raw);
      assert.equal(workbench.embedText.value, 'keep text draft');
      assert.equal(workbench.filterText.value, 'keep filter draft');
      assert.ok(!JSON.stringify(workbench.historyEntries).includes('server-secret-denial'));
      assert.ok(!workbench.errorMsg.value.includes('server-secret-denial'));
    } finally { workbench.cleanup(); }
  }
});

test('same-identity Schema/auth refresh cannot unlock a denied Vector page or dispatch again', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => { throw { response: { status: 403 } }; });
  try {
    await workbench.runSearch();
    workbench.props.index = null;
    workbench.props.indexes = [];
    assert.equal(workbench.vectorState.value, 'permission');
    await workbench.runSearch();
    workbench.props.indexes = [index()];
    workbench.props.index = index();
    assert.equal(workbench.vectorState.value, 'permission');
    workbench.props.index = index();
    workbench.props.measurement = measurement();
    workbench.auth.state = { token: 'token-a', username: 'reader' };
    await workbench.runSearch();
    assert.equal(workbench.vectorState.value, 'permission');
    assert.equal(workbench.calls.length, 1);
    workbench.props.targetDb = 'South:DB';
    assert.equal(workbench.permissionDenied.value, false);
    assert.equal(workbench.latestResult.value, null);
  } finally { workbench.cleanup(); }
});

test('current Measurement child denial locks Vector and survives Schema, authentication, view remount and empty identity ABA', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [hit()] } }));
  try {
    workbench.props.readOnly = true;
    await workbench.runSearch(); workbench.activeView.value = 'data';
    assert.equal(workbench.hits.value.length, 1);
    workbench.onMeasurementPermissionRejected({ database: 'North:DB', measurement: 'Vectors:Original', generation: workbench.dataGeneration.value });
    assert.equal(workbench.permissionDenied.value, true); assert.equal(workbench.vectorState.value, 'permission');
    assert.equal(workbench.hits.value.length, 0); assert.equal(workbench.hitRows.value.length, 0); assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.latestCommand.value, ''); assert.equal(workbench.queryVector.value.length, 0);
    assert.equal(workbench.historyEntries[0].database, 'North:DB'); assert.equal(workbench.historyEntries[0].status, 'success');
    const count = workbench.calls.length;
    workbench.props.measurement = measurement(); workbench.auth.state = { token: 'token-a', username: 'reader' };
    workbench.activeView.value = 'search'; workbench.activeView.value = 'data';
    workbench.props.targetDb = ''; workbench.props.targetDb = 'North:DB';
    workbench.connection.activeProfile.id = ''; workbench.connection.activeProfile.id = 'profile-a';
    workbench.connection.activeProfile.baseUrl = ''; workbench.connection.activeProfile.baseUrl = '/gateway';
    workbench.props.index = null; workbench.props.indexes = []; workbench.props.measurement = null;
    workbench.props.index = index(); workbench.props.indexes = [index()]; workbench.props.measurement = measurement();
    await workbench.runSearch();
    assert.equal(workbench.permissionDenied.value, true); assert.equal(workbench.calls.length, count);
  } finally { workbench.cleanup(); }
});

test('wrong-target, old-generation and old-authority child denials cannot clear a current Vector busy owner', { timeout: 5000 }, async () => {
  const deadline = Date.now() + 4500;
  for (const change of ['database-event', 'measurement-event', 'generation-event', 'view-aba', 'database-aba', 'schema', 'auth-aba', 'live-endpoint']) {
    assert.ok(Date.now() < deadline, 'Child denial matrix exceeded its wall-clock budget');
    const pending = deferred(); const workbench = await loadWorkbench(() => pending.promise);
    try {
      workbench.activeView.value = 'data';
      const denial = { database: 'North:DB', measurement: 'Vectors:Original', generation: workbench.dataGeneration.value };
      if (change === 'database-event') denial.database = 'South:DB';
      else if (change === 'measurement-event') denial.measurement = 'Other:Measurement';
      else if (change === 'generation-event') denial.generation -= 1;
      else if (change === 'view-aba') { workbench.activeView.value = 'search'; workbench.activeView.value = 'data'; }
      else if (change === 'database-aba') { workbench.props.targetDb = 'South:DB'; workbench.props.targetDb = 'North:DB'; }
      else if (change === 'schema') workbench.props.measurement.columns[0].dataType = 'float64';
      else if (change === 'auth-aba') { workbench.auth.state = { token: 'other' }; workbench.auth.state = { token: 'token-a', username: 'reader' }; }
      else workbench.auth.api.defaults.baseURL = '/new-live-endpoint';
      const generation = workbench.dataGeneration.value; const run = workbench.runSearch();
      workbench.onMeasurementPermissionRejected(denial);
      assert.equal(workbench.permissionDenied.value, false); assert.equal(workbench.searching.value, true); assert.equal(workbench.dataGeneration.value, generation);
      pending.resolve({ data: { hits: [hit(2, 'current-resource')] } }); await run;
      assert.equal(workbench.hits.value[0].timestampUtc, 2); assert.equal(workbench.latestResult.value.rows.length, 1);
    } finally { pending.resolve({ data: { hits: [] } }); workbench.cleanup(); }
  }
});

test('an unmounted parent and a hidden Measurement view reject permission notifications', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [hit()] } }));
  try {
    await workbench.runSearch();
    workbench.onMeasurementPermissionRejected({ database: 'North:DB', measurement: 'Vectors:Original', generation: workbench.dataGeneration.value });
    assert.equal(workbench.permissionDenied.value, false); assert.equal(workbench.hits.value.length, 1);
    workbench.activeView.value = 'data';
    const denial = { database: 'North:DB', measurement: 'Vectors:Original', generation: workbench.dataGeneration.value };
    workbench.cleanup(); workbench.onMeasurementPermissionRejected(denial);
    assert.equal(workbench.permissionDenied.value, false);
  } finally { workbench.cleanup(); }
});

test('external deny isolates an in-flight response and readonly still permits raw search', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    workbench.props.readOnly = true;
    assert.equal(workbench.vectorState.value, 'readonly');
    const run = workbench.runSearch();
    workbench.props.permissionDenied = true;
    pending.resolve({ data: { hits: [hit(1, 'old-secret')] } });
    await run;
    assert.equal(workbench.calls.length, 1);
    assert.equal(workbench.vectorState.value, 'permission');
    assert.equal(workbench.latestResult.value, null);
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 1);
  } finally { workbench.cleanup(); }
});

test('Text embed without an index-bound Profile preserves its draft and cannot dispatch embed or search', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }));
  try {
    workbench.queryMode.value = 'text';
    workbench.embedText.value = 'keep original text';
    await workbench.embedTextToVector();
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 0);
    assert.equal(workbench.embedText.value, 'keep original text');
    assert.match(workbench.errorMsg.value, /Profile/u);
    workbench.queryMode.value = 'raw';
    await workbench.runSearch();
    assert.equal(workbench.calls.length, 1);
  } finally { workbench.cleanup(); }
});

test('Vector rejects malformed retained hit/envelope DTOs with only a sanitized error result/history', { timeout: 5000 }, async () => {
  const cases = [null, { hits: 'server-secret' }, { hits: [{ ...hit(), distance: Infinity }] },
    { hits: [{ ...hit(), timestampUtc: 'server-secret' }] }, { hits: [{ ...hit(), tags: [{ key: 'server-secret', value: {} }] }] }];
  for (const response of cases) {
    const workbench = await loadWorkbench(async () => ({ data: response }));
    try {
      await workbench.runSearch();
      assert.equal(workbench.vectorState.value, 'error');
      assert.equal(workbench.hits.value.length, 0);
      assert.equal(workbench.latestResult.value.rows.length, 0);
      assert.equal(workbench.historyEntries[0].status, 'error');
      assert.ok(!JSON.stringify(workbench.latestResult.value).includes('server-secret'));
      assert.ok(!JSON.stringify(workbench.historyEntries).includes('server-secret'));
    } finally { workbench.cleanup(); }
  }
});

test('Vector runtime state covers normal, empty, error, permission, readonly and longContent', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }));
  try {
    assert.equal(workbench.vectorState.value, 'normal');
    await workbench.runSearch();
    assert.equal(workbench.vectorState.value, 'empty');
    workbench.errorMsg.value = 'safe failure';
    assert.equal(workbench.vectorState.value, 'error');
    workbench.props.readOnly = true;
    assert.equal(workbench.vectorState.value, 'readonly');
    workbench.props.permissionDenied = true;
    assert.equal(workbench.vectorState.value, 'permission');
    workbench.props.permissionDenied = false;
    workbench.props.readOnly = false;
    workbench.errorMsg.value = '';
    workbench.hitsTruncated.value = true;
    assert.equal(workbench.vectorState.value, 'longContent');
  } finally { workbench.cleanup(); }
});

test('same-name complete Schema mutation and authentication ABA synchronously invalidate the old Vector request', { timeout: 5000 }, async () => {
  for (const change of ['dimension', 'params', 'measurement-schema', 'auth', 'profile-endpoint']) {
    const pending = deferred();
    const workbench = await loadWorkbench(() => pending.promise);
    try {
      const run = workbench.runSearch();
      if (change === 'dimension') workbench.props.index.dimension = 4;
      else if (change === 'params') workbench.props.index.params[0].value = '32';
      else if (change === 'measurement-schema') workbench.props.measurement.columns[0].name = 'ChangedTime';
      else if (change === 'profile-endpoint') workbench.connection.activeProfile.baseUrl = '/other';
      else {
        workbench.auth.state = { token: 'other', username: 'other' };
        workbench.auth.state = { token: 'token-a', username: 'reader' };
      }
      pending.resolve({ data: { hits: [hit(1, 'old-secret')] } });
      await run;
      assert.equal(workbench.calls[0].signal.aborted, true);
      assert.equal(workbench.hits.value.length, 0);
      assert.equal(workbench.historyEntries.length, 0);
    } finally { workbench.cleanup(); }
  }
});

test('live markRaw API endpoint is read at capture/completion rather than cached computed defaults', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    const run = workbench.runSearch();
    workbench.auth.api.defaults.baseURL = '/new-live-endpoint';
    const snapshot = workbench.captureContext();
    assert.equal(snapshot.endpoint, '/new-live-endpoint');
    assert.equal(snapshot.requestApi.defaults.baseURL, '/new-live-endpoint');
    pending.resolve({ data: { hits: [hit(1, 'old-secret')] } });
    await run;
    assert.equal(workbench.hits.value.length, 0);
    assert.equal(workbench.historyEntries.length, 0);
  } finally { workbench.cleanup(); }
});

test('synchronous auth change aborts before the real Axios adapter dispatches the actual Vector API helper', { timeout: 5000 }, async () => {
  let dispatched = 0;
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }), {
    createApiClient(getToken) {
      const api = axios.create({ adapter: async (config) => {
        dispatched += 1;
        return { config, status: 200, statusText: 'OK', headers: {}, data: { hits: [] } };
      } });
      api.interceptors.request.use((config) => { config.headers.Authorization = `Bearer ${getToken()}`; return config; });
      return api;
    },
  });
  try {
    const run = workbench.runSearch();
    workbench.auth.state = { token: 'other', username: 'other' };
    await run;
    assert.equal(dispatched, 0);
    assert.equal(workbench.latestResult.value, null);
  } finally { workbench.cleanup(); }
});

test('real Axios snapshot keeps its captured Token after auth changes before asynchronous interceptor execution', { timeout: 5000 }, async () => {
  const interceptorReached = deferred();
  const release = deferred();
  let configSeen;
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }), {
    createApiClient(getToken) {
      const api = axios.create({ adapter: async (config) => {
        configSeen = config;
        return { config, status: 200, statusText: 'OK', headers: {}, data: { hits: [] } };
      } });
      api.interceptors.request.use(async (config) => {
        interceptorReached.resolve();
        await release.promise;
        config.headers.Authorization = `Bearer ${getToken()}`;
        return config;
      });
      return api;
    },
  });
  try {
    const snapshot = workbench.captureContext();
    // Exercise the actual API helper with the captured client independently of
    // the page-owned cancellation test above, so a changed Token is observed
    // by the delayed interceptor without an already-aborted request.
    const run = workbench.searchPreview(snapshot.requestApi, snapshot.database, {
      measurement: snapshot.measurement, column: snapshot.column, query: [1, 0, 0], topK: 3,
    });
    await interceptorReached.promise;
    workbench.auth.state = { token: 'token-other', username: 'other' };
    release.resolve();
    await run;
    assert.equal(configSeen.headers.Authorization, 'Bearer token-a');
    assert.equal(configSeen.baseURL, '/gateway');
    assert.equal(JSON.parse(configSeen.data).measurement, 'Vectors:Original');
    assert.equal(workbench.historyEntries.length, 0);
  } finally { release.resolve(); workbench.cleanup(); }
});

function memoryRenderer() {
  const node = (type, text = '') => ({ type, text, children: [], parent: null, props: {} });
  return vue.createRenderer({
    createElement: (type) => node(type), createText: (text) => node('#text', text), createComment: (text) => node('#comment', text),
    setText: (item, text) => { item.text = text; }, setElementText: (item, text) => { item.text = text; item.children = []; },
    patchProp: (item, key, _, value) => { item.props[key] = value; },
    insert(item, parent, anchor = null) {
      if (item.parent) { const offset = item.parent.children.indexOf(item); if (offset >= 0) item.parent.children.splice(offset, 1); }
      item.parent = parent;
      const offset = anchor ? parent.children.indexOf(anchor) : -1;
      parent.children.splice(offset < 0 ? parent.children.length : offset, 0, item);
    },
    remove(item) { if (item.parent) { const offset = item.parent.children.indexOf(item); if (offset >= 0) item.parent.children.splice(offset, 1); } item.parent = null; },
    parentNode: (item) => item.parent,
    nextSibling: (item) => item.parent?.children[item.parent.children.indexOf(item) + 1] ?? null,
  });
}

test('compiled Vector template passes readonly/permission and rebuilds Measurement child on identity/Schema generations', { timeout: 5000 }, async () => {
  const children = [];
  const child = vue.defineComponent({
    props: ['targetDb', 'measurement', 'measurements', 'tables', 'loading', 'readOnly', 'permissionDenied', 'permissionGeneration'],
    emits: ['permissionRejected'],
    setup(props, { emit }) {
      const state = { props, unmounted: false, reject: (denial) => emit('permissionRejected', denial) };
      children.push(state);
      vue.onBeforeUnmount(() => { state.unmounted = true; });
      return () => h('div');
    },
  });
  const workbench = await loadWorkbench(async () => ({ data: { hits: [] } }), { mount: true, child });
  const renderer = memoryRenderer();
  const root = { children: [] };
  const app = renderer.createApp(vue.defineComponent({ setup: () => () => h(workbench.default, { ...workbench.props }) }));
  try {
    app.mount(root);
    // Invoke the compiled tab contract through the actual component's child VNode event.
    const instance = app._instance.subTree.component;
    const findTab = (vnode) => {
      if (vnode.type?.name === 'WorkbenchSectionTabs') return vnode;
      const nested = Array.isArray(vnode.children) ? vnode.children : [];
      return nested.map(findTab).find(Boolean);
    };
    const tabs = findTab(instance.subTree);
    tabs.props['onUpdate:modelValue']('data');
    await nextTick();
    assert.equal(children.length, 1);
    assert.equal(children[0].props.targetDb, 'North:DB');
    assert.equal(children[0].props.measurement.name, 'Vectors:Original');
    assert.equal(typeof children[0].props.permissionGeneration, 'number');
    assert.equal(children[0].props.readOnly, false);
    workbench.props.readOnly = true;
    await nextTick();
    assert.equal(children[0].unmounted, true);
    assert.equal(children.at(-1).props.readOnly, true);
    const beforeSchema = children.length;
    workbench.props.measurement.columns[0].dataType = 'float64';
    await nextTick();
    assert.ok(children.length > beforeSchema);
    workbench.props.targetDb = 'South:DB';
    await nextTick();
    assert.equal(children.at(-1).props.targetDb, 'South:DB');
    const current = children.at(-1);
    current.reject({ database: 'South:DB', measurement: 'Vectors:Original', generation: current.props.permissionGeneration });
    await nextTick();
    assert.equal(children.at(-1).props.permissionDenied, true);
    assert.equal(children.at(-1).props.measurement, null);
    assert.equal(children.at(-1).props.measurements.length, 0);
  } finally { app.unmount(); workbench.cleanup(); }
});
