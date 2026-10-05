import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import { computed, h, markRaw, nextTick, reactive, ref, watch } from 'vue';

const sourcePath = new URL('../src/components/FullTextSearchWorkbench.vue', import.meta.url);
const source = readFileSync(sourcePath, 'utf8');
const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/u)[1];

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

const index = (collection = 'Docs:Original', name = 'Search:Original') => ({
  collection, name, fields: ['title', 'body'], tokenizer: 'unicode', documentCount: 3, termCount: 8,
});
const rebuildResponse = () => ({
  operation: 'rebuild_index', status: 'ok', success: true, message: 'server-secret', completedUtc: '2026-10-06T00:00:00Z',
  checks: [{ name: 'index', status: 'ok', message: 'server-secret' }],
  index: { model: 'document', owner: 'Docs:Original', name: 'Search:Original', kind: 'fulltext', mode: 'sync_touch', planned: false, rebuildable: true, documentCount: 3 },
});

async function loadWorkbench(post, operationOverrides = {}) {
  const props = reactive({ targetDb: 'North:DB', index: index(), indexes: [index()], loading: false, readOnly: false, permissionDenied: false });
  const auth = reactive({ state: { token: 'token-a', username: 'reader' }, api: markRaw({ post, defaults: { baseURL: '/gateway' } }) });
  const connection = reactive({
    activeProfileId: 'profile-a',
    activeDatabase: 'North:DB',
    activeProfile: { id: 'profile-a', name: 'Connection A', baseUrl: '/gateway' },
  });
  const historyEntries = [];
  const cleanups = [];
  const calls = [];
  const client = (getToken) => ({
    defaults: { baseURL: '/gateway' },
    async post(path, request, config) {
      // Match the JSON transport boundary: staged document items are Vue proxies,
      // which structuredClone rejects even though Axios can serialize them.
      calls.push({ path, request: JSON.parse(JSON.stringify(request)), token: getToken(), signal: config?.signal });
      return post(path, request, config);
    },
  });
  const dependencies = {
    vue: synthetic({ computed, h, ref, watch, onBeforeUnmount: (cleanup) => cleanups.push(cleanup) }),
    'naive-ui': synthetic({
      ...Object.fromEntries(['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NSelect', 'NSpace', 'NTag', 'NText']
        .map((name) => [name, name])),
      useMessage: () => ({ success() {}, warning() {} }),
    }),
    '@/api/documents': synthetic({
      insertManyDocuments: async (api, db, collection, request) => (await api.post(`/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/insert-many`, request)).data,
      updateOneDocument: async (api, db, collection, request) => (await api.post(`/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/update-one`, request)).data,
      ...operationOverrides.documents,
    }),
    '@/api/client': synthetic({ createApiClient: operationOverrides.createApiClient ?? client }),
    '@/api/fulltext': synthetic({}),
    '@/api/management': synthetic({}),
    '@/api/schema': synthetic({ runMaintenance: async (api, db, request) => (await api.post(`/v1/db/${encodeURIComponent(db)}/maintenance`, request)).data, ...operationOverrides.schema }),
    '@/stores/auth': synthetic({ useAuthStore: () => auth }),
    '@/stores/connections': synthetic({ useConnectionsStore: () => connection }),
    '@/stores/workbenchHistory': synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => historyEntries.push(entry) }) }),
    '@/utils/writeApproval': synthetic({ createWriteApprovalPlan: (value) => value }),
    '@/utils/sqlWorkbench': synthetic({ formatSqlIdentifier: (value) => `"${value}"` }),
    '@/api/sql': synthetic({ quote: (value) => `"${value}"` }),
  };
  for (const name of ['WorkbenchHistoryDrawer', 'WorkbenchResultPanel', 'WorkbenchSectionTabs', 'WriteApprovalPanel']) {
    dependencies[`@/components/${name}.vue`] = synthetic({ default: name });
  }
  const prelude = `const defineProps = () => globalThis.__fulltextProps;
    const withDefaults = (value) => value;
    const defineEmits = () => () => {};`;
  dependencies.fixture = synthetic({ props });
  const injected = prelude.replace('globalThis.__fulltextProps', 'fixtureProps');
  const exports = `export { runSearch, runAnalyze, stageRebuild, confirmPendingWrite, hits, hitsTruncated,
    latestResult, latestCommand, permissionLocked, permissionDenied, fulltextState, queryText, previewPlan, errorMsg,
    contextSnapshot, captureContext, topK, field, queryKind, mode, analyzeTokens, documentsById, importText, importMode,
    stageFullTextImport, resultField, resultQuery, pendingImportItems };`;
  const module = new SourceTextModule(stripTypeScriptTypes(
    `import { props as fixtureProps } from 'fixture';\n${injected}\n${script}\n${exports}`,
    { mode: 'transform' },
  ), { identifier: sourcePath.href });
  await module.link((specifier) => {
    assert.ok(dependencies[specifier], `Unexpected dependency: ${specifier}`);
    return dependencies[specifier];
  });
  await module.evaluate({ timeout: 3000 });
  return { ...module.namespace, props, auth, connection, calls, historyEntries, cleanup: () => cleanups.forEach((item) => item()) };
}

test('FullText migration exposes original-name context and bounded permission/result contracts', () => {
  assert.match(source, /data-testid="workbench-fulltext"/u);
  assert.match(source, /:data-database="contextSnapshot\.database"/u);
  assert.match(source, /:data-resource-key="activeIndexKey"/u);
  assert.match(source, /data-testid="fulltext-context"/u);
  assert.match(source, /data-testid="fulltext-permission-lock"/u);
  assert.match(source, /AbortController/u);
  assert.match(source, /FullTextLocalHitBudget = 100/u);
  assert.match(source, /truncated to/u);
  assert.match(source, /pendingWriteContext/u);
  assert.match(source, /isWriteContextCurrent\(stagedContext, epoch\)/u);
});

test('late FullText search results cannot populate a different database or index', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench((path) => {
    if (path.endsWith('/fulltext/search-preview')) return pending.promise;
    if (path.endsWith('/fulltext/analyze')) return Promise.resolve({ data: { tokens: [] } });
    if (path.endsWith('/documents/Docs%3AOriginal/find')) return Promise.resolve({ data: { collection: 'Docs:Original', documents: [] } });
    return Promise.resolve({ data: {} });
  });
  try {
    const run = workbench.runSearch();
    workbench.props.targetDb = 'South:DB';
    await nextTick();
    pending.resolve({ data: { hits: [{ documentId: 'north-secret', score: 9 }] } });
    await run;
    assert.equal(workbench.hits.value.length, 0);
    assert.equal(workbench.contextSnapshot.value.database, 'South:DB');
  } finally { workbench.cleanup(); }
});

test('401/403 clears FullText payloads while retaining query draft and sanitizing history', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async (path) => {
    if (path.endsWith('/fulltext/search-preview')) {
      throw { response: { status: 403, data: { message: 'server-secret-document' } } };
    }
    return { data: {} };
  });
  try {
    workbench.queryText.value = 'keep this draft';
    await workbench.runSearch();
    assert.equal(workbench.permissionLocked.value, true);
    assert.equal(workbench.hits.value.length, 0);
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.queryText.value, 'keep this draft');
    assert.ok(workbench.historyEntries.every((entry) => !JSON.stringify(entry).includes('server-secret-document')));
  } finally { workbench.cleanup(); }
});

test('search result is capped before pagination and marks truncation', { timeout: 5000 }, async () => {
  const serverHits = Array.from({ length: 1002 }, (_, i) => ({ documentId: `doc-${i}`, score: 1002 - i }));
  const workbench = await loadWorkbench(async (path) => {
    if (path.endsWith('/fulltext/search-preview')) return { data: { hits: serverHits } };
    if (path.endsWith('/fulltext/analyze')) return { data: { tokens: [] } };
    if (path.includes('/documents/')) return { data: { collection: 'Docs:Original', documents: [] } };
    return { data: {} };
  });
  try {
    await workbench.runSearch();
    assert.equal(workbench.hits.value.length, 20);
    assert.equal(workbench.hitsTruncated.value, true);
    assert.equal(workbench.latestResult.value.end.truncated, true);
  } finally { workbench.cleanup(); }
});

test('rebuild approval cannot execute after a database/index context change', { timeout: 5000 }, async () => {
  let maintenanceCalls = 0;
  const workbench = await loadWorkbench(async () => {
    maintenanceCalls += 1;
    return { data: {} };
  });
  try {
    workbench.stageRebuild();
    assert.ok(workbench.previewPlan.value);
    workbench.props.targetDb = 'Other:DB';
    await nextTick();
    await workbench.confirmPendingWrite();
    assert.equal(maintenanceCalls, 0);
    assert.equal(workbench.previewPlan.value, null);
  } finally { workbench.cleanup(); }
});

test('search freezes TopK, query and field while the editor is changed', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(async (path) => {
    if (path.endsWith('/search-preview')) return pending.promise;
    if (path.endsWith('/find')) return { data: { collection: 'Docs:Original', documents: [] } };
    return { data: { tokens: [] } };
  });
  try {
    workbench.topK.value = 3;
    workbench.field.value = 'title';
    workbench.queryText.value = 'original query';
    const run = workbench.runSearch();
    workbench.topK.value = 99;
    workbench.field.value = 'body';
    workbench.queryText.value = 'new draft';
    pending.resolve({ data: { hits: Array.from({ length: 4 }, (_, i) => ({ documentId: `doc-${i}`, score: i })) } });
    await run;
    assert.equal(workbench.hits.value.length, 3);
    assert.equal(workbench.resultField.value, 'title');
    assert.equal(workbench.resultQuery.value, 'original query');
    assert.equal(workbench.calls.find((call) => call.path.endsWith('/analyze')).request.text, 'original query');
    assert.equal(workbench.historyEntries[0].completeness, 'truncated');
  } finally { workbench.cleanup(); }
});

test('late Analyzer and hit-document payloads are isolated on context change and unmount', { timeout: 5000 }, async () => {
  for (const kind of ['analyze', 'find']) {
    const pending = deferred();
    const childStarted = deferred();
    const workbench = await loadWorkbench(async (path) => {
      if (path.endsWith(`/${kind}`)) {
        childStarted.resolve();
        return pending.promise;
      }
      if (path.endsWith('/search-preview')) return { data: { hits: [{ documentId: 'doc-a', score: 1 }] } };
      return { data: { tokens: [] } };
    });
    try {
      const run = kind === 'analyze' ? workbench.runAnalyze(false) : workbench.runSearch();
      await childStarted.promise;
      workbench.props.targetDb = 'South:DB';
      workbench.cleanup();
      pending.resolve({ data: kind === 'analyze' ? { tokens: [{ text: 'old-secret', startOffset: 0, endOffset: 10, positionIncrement: 1 }] }
        : { collection: 'Docs:Original', documents: [{ id: 'doc-a', version: 1, document: { secret: 'old-secret' } }] } });
      await run;
      assert.equal(workbench.analyzeTokens.value.length, 0);
      assert.deepEqual(workbench.documentsById.value, {});
      assert.equal(workbench.latestResult.value, null);
    } finally { workbench.cleanup(); }
  }
});

test('same-token authentication ABA discards the old search and preserves a denial through schema/auth refresh', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    const run = workbench.runSearch();
    workbench.auth.state = { token: 'token-other', username: 'other' };
    workbench.auth.state = { token: 'token-a', username: 'reader' };
    pending.resolve({ data: { hits: [{ documentId: 'old-secret', score: 1 }] } });
    await run;
    assert.equal(workbench.hits.value.length, 0);
    workbench.permissionLocked.value = true;
    workbench.props.index = index();
    workbench.auth.state = { token: 'token-a', username: 'reader' };
    assert.equal(workbench.permissionDenied.value, true);
    assert.equal(workbench.fulltextState.value, 'permission');
  } finally { workbench.cleanup(); }
});

test('read-only allows reading while refusing import/rebuild staging and external deny clears pending payload', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench(async (path) => {
    if (path.endsWith('/search-preview')) return { data: { hits: [] } };
    return { data: { tokens: [] } };
  });
  try {
    workbench.props.readOnly = true;
    assert.equal(workbench.fulltextState.value, 'readonly');
    workbench.importText.value = '{"_id":"doc-a","secret":"draft"}';
    workbench.stageFullTextImport();
    workbench.stageRebuild();
    assert.equal(workbench.previewPlan.value, null);
    await workbench.runSearch();
    assert.equal(workbench.latestResult.value.end.rowCount, 0);
    workbench.props.readOnly = false;
    workbench.stageFullTextImport();
    assert.ok(workbench.previewPlan.value);
    workbench.props.permissionDenied = true;
    assert.equal(workbench.pendingImportItems.value.length, 0);
    assert.equal(workbench.importText.value, '');
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.fulltextState.value, 'permission');
  } finally { workbench.cleanup(); }
});

test('rebuild confirmation is consumed once and an in-flight context change records unknown for its original identity', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    workbench.stageRebuild();
    const run = workbench.confirmPendingWrite();
    await workbench.confirmPendingWrite();
    assert.equal(workbench.calls.length, 1);
    workbench.props.targetDb = 'South:DB';
    workbench.props.targetDb = 'North:DB';
    pending.resolve({ data: rebuildResponse() });
    await run;
    assert.equal(workbench.previewPlan.value, null);
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.historyEntries[0].status, 'unknown');
    assert.equal(workbench.historyEntries[0].database, 'North:DB');
    assert.ok(!JSON.stringify(workbench.historyEntries).includes('server-secret'));
  } finally { workbench.cleanup(); }
});

test('import freezes its target and stops subsequent batches after a database ABA', { timeout: 5000 }, async () => {
  const pending = deferred();
  const firstBatchStarted = deferred();
  const workbench = await loadWorkbench((path) => {
    assert.ok(path.endsWith('/insert-many'));
    firstBatchStarted.resolve();
    return pending.promise;
  });
  try {
    workbench.importText.value = JSON.stringify(Array.from({ length: 101 }, (_, i) => ({ _id: `doc-${i}` })));
    workbench.stageFullTextImport();
    const run = workbench.confirmPendingWrite();
    await firstBatchStarted.promise;
    workbench.props.targetDb = 'South:DB';
    workbench.props.targetDb = 'North:DB';
    assert.equal(workbench.importText.value, '');
    assert.equal(workbench.pendingImportItems.value.length, 0);
    assert.equal(workbench.queryText.value, 'pump alarm');
    pending.resolve({ data: { collection: 'Docs:Original', inserted: 100, matched: 0, modified: 0, deleted: 0 } });
    await run;
    assert.equal(workbench.calls.length, 1);
    assert.ok(workbench.calls[0].path.includes('/North%3ADB/'));
    assert.equal(workbench.historyEntries[0].status, 'unknown');
    assert.equal(workbench.previewPlan.value, null);
  } finally { workbench.cleanup(); }
});

test('transport, malformed and planned rebuild results are unknown without restoring approval; canonical terminal results are recognized', { timeout: 5000 }, async () => {
  const cases = [
    { reject: true, status: 'unknown' },
    { data: { success: true }, status: 'unknown' },
    { data: { ...rebuildResponse(), status: 'planned', index: { ...rebuildResponse().index, planned: true } }, status: 'unknown' },
    { data: { ...rebuildResponse(), operation: 'other' }, status: 'unknown' },
    { data: { ...rebuildResponse(), index: { ...rebuildResponse().index, owner: 'Other:Collection' } }, status: 'unknown' },
    { data: rebuildResponse(), status: 'success' },
    { data: { operation: 'rebuild_index', status: 'failed', success: false, message: 'server-secret', completedUtc: '2026-10-06T00:00:00Z', checks: [{ name: 'request', status: 'error', message: 'server-secret' }] }, status: 'error' },
  ];
  for (const fixture of cases) {
    const workbench = await loadWorkbench(async () => {
      if (fixture.reject) throw new Error('server-secret transport');
      return { data: fixture.data };
    });
    try {
      workbench.stageRebuild();
      await workbench.confirmPendingWrite();
      await workbench.confirmPendingWrite();
      assert.equal(workbench.calls.length, 1);
      assert.equal(workbench.previewPlan.value, null);
      assert.equal(workbench.historyEntries[0].status, fixture.status);
      assert.ok(!workbench.errorMsg.value.includes('server-secret'));
      assert.ok(!JSON.stringify(workbench.historyEntries).includes('server-secret'));
    } finally { workbench.cleanup(); }
  }
});

test('same-name schema mutation and live API endpoint changes invalidate pending work', { timeout: 5000 }, async () => {
  const pending = deferred();
  const workbench = await loadWorkbench(() => pending.promise);
  try {
    workbench.stageRebuild();
    workbench.props.index.fields = ['changed-field'];
    await workbench.confirmPendingWrite();
    assert.equal(workbench.calls.length, 0);
    const run = workbench.runSearch();
    workbench.auth.api.defaults.baseURL = '/new-endpoint';
    const snapshot = workbench.captureContext();
    assert.equal(snapshot.endpoint, '/new-endpoint');
    assert.equal(snapshot.requestApi.defaults.baseURL, '/new-endpoint');
    pending.resolve({ data: { hits: [{ documentId: 'old-secret', score: 1 }] } });
    await run;
    assert.equal(workbench.hits.value.length, 0);
    assert.equal(workbench.historyEntries.length, 0);
  } finally { workbench.cleanup(); }
});

test('starting a new search isolates late child Find and Analyzer requests even if abort is ignored', { timeout: 5000 }, async () => {
  for (const kind of ['find', 'analyze']) {
    const pending = deferred();
    const childStarted = deferred();
    let searches = 0;
    let childRequests = 0;
    const workbench = await loadWorkbench(async (path, request) => {
      if (path.endsWith('/search-preview')) {
        searches += 1;
        return { data: { hits: [{ documentId: searches === 1 ? 'old-hit' : 'current-hit', score: 1 }] } };
      }
      if (path.endsWith(`/${kind}`)) {
        childRequests += 1;
        if (childRequests === 1) {
          childStarted.resolve();
          return pending.promise;
        }
      }
      if (path.endsWith('/find')) {
        return { data: { collection: 'Docs:Original', documents: request.ids.map((id) => ({ id, version: 1, document: { title: id } })) } };
      }
      return { data: { tokens: [{ text: 'current-token', startOffset: 0, endOffset: 13, positionIncrement: 1 }] } };
    });
    try {
      const oldRun = workbench.runSearch();
      await childStarted.promise;
      const currentRun = workbench.runSearch();
      await currentRun;
      pending.resolve({ data: kind === 'find'
        ? { collection: 'Docs:Original', documents: [{ id: 'old-hit', version: 1, document: { secret: 'old-secret' } }] }
        : { tokens: [{ text: 'old-secret', startOffset: 0, endOffset: 10, positionIncrement: 1 }] } });
      await oldRun;
      assert.equal(workbench.hits.value[0].documentId, 'current-hit');
      assert.ok(!JSON.stringify(workbench.documentsById.value).includes('old-secret'));
      assert.ok(!JSON.stringify(workbench.latestResult.value).includes('old-secret'));
    } finally { workbench.cleanup(); }
  }
});

test('synchronous identity change aborts an approved write before the real Axios adapter dispatches', { timeout: 5000 }, async () => {
  let dispatched = 0;
  const workbench = await loadWorkbench(async () => ({ data: {} }), {
    createApiClient(getToken) {
      const api = axios.create({ adapter: async (config) => {
        dispatched += 1;
        return { config, status: 200, statusText: 'OK', headers: {}, data: rebuildResponse() };
      } });
      api.interceptors.request.use((config) => {
        config.headers.Authorization = `Bearer ${getToken()}`;
        return config;
      });
      return api;
    },
  });
  try {
    workbench.stageRebuild();
    const run = workbench.confirmPendingWrite();
    workbench.auth.state = { token: 'token-other', username: 'other' };
    await run;
    assert.equal(dispatched, 0);
    assert.equal(workbench.previewPlan.value, null);
    assert.equal(workbench.historyEntries[0].status, 'unknown');
    assert.equal(workbench.historyEntries[0].database, 'North:DB');
  } finally { workbench.cleanup(); }
});
