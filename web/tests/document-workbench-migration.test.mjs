import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import { computed, h, nextTick, reactive, ref, watch } from 'vue';

const sourcePath = new URL('../src/components/DocumentCollectionWorkbench.vue', import.meta.url);
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

const collection = (name = 'Device:Profiles') => ({ name, jsonIndexes: [], fullTextIndexes: [], validator: null });
const findResponse = (id, hasMore = false) => ({
  collection: 'Device:Profiles', documents: id ? [{ id, version: 1, document: { secret: id } }] : [],
  count: id ? 1 : 0, skip: 0, hasMore,
});

async function loadWorkbench(overrides = {}, getStudioNativeBridge = async () => null) {
  const props = reactive({ targetDb: 'North:DB', collection: null, collections: [], loading: false, readOnly: false, permissionDenied: false });
  const connection = reactive({ activeProfileId: 'Profile:A', activeBaseUrl: '/', activeProfile: { name: 'Connection A' } });
  const historyEntries = [];
  const cleanups = [];
  const auth = { api: {} };
  const documents = {
    aggregateDocuments: async () => ({ documents: [], count: 0 }),
    bulkWriteDocuments: async () => ({ collection: 'Device:Profiles', inserted: 1 }),
    countDocuments: async () => ({ collection: 'Device:Profiles', count: 1 }),
    createDocumentCollection: async () => ({ collection: 'Device:Profiles', status: 'created' }),
    deleteManyDocuments: async () => ({ deleted: 1 }), deleteOneDocument: async () => ({ deleted: 1 }),
    distinctDocuments: async () => ({ path: '$.site', values: [] }),
    dropDocumentCollection: async () => ({ collection: 'Device:Profiles', status: 'dropped' }),
    dropDocumentValidator: async () => ({ collection: 'Device:Profiles', status: 'dropped' }),
    findDocuments: async () => findResponse('current'), insertOneDocument: async () => ({ inserted: 1 }),
    setDocumentValidator: async () => ({ collection: 'Device:Profiles', status: 'updated' }),
    updateOneDocument: async () => ({ modified: 1 }),
    ...overrides,
  };
  const names = ['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NProgress', 'NSelect', 'NSpace', 'NTab', 'NTabs', 'NTag', 'NText'];
  const dependencies = {
    vue: synthetic({ computed, h, ref, watch, onBeforeUnmount: (cleanup) => cleanups.push(cleanup) }),
    'naive-ui': synthetic({ ...Object.fromEntries(names.map((name) => [name, name])), useMessage: () => ({ success() {}, warning() {} }) }),
    'lucide-vue-next': synthetic({ Activity: null, FolderOpen: null, ListFilter: null, PencilLine: null, Plus: null, X: null }),
    '@/api/documents': synthetic(documents),
    '@/api/studioNativeBridge': synthetic({ getStudioNativeBridge }),
    '@/api/schema': synthetic({ runMaintenance: async () => ({ success: true }) }),
    '@/stores/auth': synthetic({ useAuthStore: () => auth }),
    '@/stores/connections': synthetic({ useConnectionsStore: () => connection }),
    '@/stores/workbenchHistory': synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => historyEntries.push(entry) }) }),
    '@/utils/resultExport': synthetic({ downloadText() {}, safeFileStem: (value) => value }),
    '@/utils/writeApproval': synthetic({ createWriteApprovalPlan: (value) => value }),
  };
  for (const name of ['WorkbenchHistoryDrawer', 'DocumentAdvancedWorkbench', 'WorkbenchResultPanel', 'WorkbenchSectionTabs', 'WriteApprovalPanel']) {
    dependencies[`@/components/${name}.vue`] = synthetic({ default: name });
  }
  const prelude = `const defineProps = () => globalThis.__documentProps;
    const withDefaults = (value) => value;
    const defineEmits = () => () => {};`;
  // Each module gets its own props through an injected SyntheticModule, avoiding global fixture state.
  dependencies.fixture = synthetic({ props });
  const injected = prelude.replace('globalThis.__documentProps', 'fixtureProps');
  const exports = `export { documentState, stateDescriptor, rows, latestResult, latestCommand, queryBusy, countBusy,
    editId, editJson, importText, importMode, importProgress, importErrors, errorMsg, importFileInput,
    onImportFileSelected, pickImportFile, cancelDocumentImport, pendingOperations, previewPlan, permissionDenied, hasMore, canLoadNext,
    runFind, runCount, runDistinct, runAggregate, stageInsertDocument, stageDeleteSelected, stageImportDocuments,
    confirmPendingOperations, applyFindResponse, clearResourcePayload, captureContext, parseImportDocuments };`;
  const module = new SourceTextModule(stripTypeScriptTypes(`import { props as fixtureProps } from 'fixture';\n${injected}\n${script}\n${exports}`, { mode: 'transform' }), { identifier: sourcePath.href });
  await module.link((specifier) => {
    assert.ok(dependencies[specifier], `Unexpected dependency: ${specifier}`);
    return dependencies[specifier];
  });
  await module.evaluate({ timeout: 3000 });
  return { ...module.namespace, props, connection, historyEntries, cleanup: () => cleanups.forEach((item) => item()) };
}

test('Document migration keeps database/original collection identity and the existing shell, tabs and approvals', { timeout: 5000 }, () => {
  for (const zone of ['toolbar', 'tabs', 'approval', 'center', 'context', 'result', 'status']) {
    assert.ok(source.includes(`data-zone="${zone}"`), `${zone} anchor`);
  }
  assert.match(source, /data-shell="five-zone"/u);
  assert.match(source, /:data-database="targetDb"/u);
  assert.match(source, /:data-resource-key="activeCollectionName"/u);
  assert.match(source, /<WriteApprovalPanel[\s\S]*?@confirm="confirmPendingOperations"/u);
  assert.match(source, /:key="resourceIdentity"/u);
  for (const key of ['documents', 'query', 'update', 'validator', 'indexes', 'changeFeed', 'import']) {
    assert.ok(source.includes(`key: '${key}'`), `${key} legacy section`);
  }
});

test('Document states hide denied payloads and retain read-only browsing with a bounded preview', { timeout: 5000 }, async () => {
  const workbench = await loadWorkbench();
  try {
    assert.equal(workbench.documentState.value, 'empty');
    workbench.props.collection = collection();
    await nextTick();
    await nextTick();
    assert.equal(workbench.documentState.value, 'normal');
    workbench.props.readOnly = true;
    assert.equal(workbench.documentState.value, 'readonly');
    workbench.editId.value = 'id';
    workbench.editJson.value = '{"secret":"value"}';
    workbench.stageInsertDocument();
    assert.equal(workbench.pendingOperations.value.length, 0);
    workbench.props.readOnly = false;
    const documents = Array.from({ length: 1001 }, (_, index) => ({ id: `doc-${index}`, version: 1, document: {} }));
    workbench.applyFindResponse({ ...findResponse(), documents, hasMore: true }, false, 1, 'find');
    assert.equal(workbench.documentState.value, 'longContent');
    assert.equal(workbench.rows.value.length, 1000);
    assert.equal(workbench.canLoadNext.value, false);
    assert.equal(workbench.latestResult.value.end.truncated, true);
    workbench.props.permissionDenied = true;
    assert.equal(workbench.documentState.value, 'permission');
    assert.equal(workbench.rows.value.length, 0);
    assert.equal(workbench.latestResult.value, null);
    assert.equal(workbench.editId.value, '');
    assert.equal(workbench.editJson.value.includes('secret'), false);
  } finally { workbench.cleanup(); }
});

test('late find/count results and failures cannot populate a different database with the same collection spelling', { timeout: 5000 }, async () => {
  const oldFind = deferred();
  const oldCount = deferred();
  const workbench = await loadWorkbench({
    findDocuments: async (_api, database) => database === 'North:DB' ? oldFind.promise : findResponse('south-only'),
    countDocuments: async (_api, database) => database === 'North:DB' ? oldCount.promise : ({ collection: 'Device:Profiles', count: 2 }),
  });
  try {
    workbench.props.collection = collection();
    workbench.props.targetDb = 'South:DB';
    await nextTick();
    await nextTick();
    oldFind.resolve(findResponse('north-secret'));
    oldCount.reject({ response: { status: 403, data: { error: 'forbidden' } } });
    await nextTick();
    await nextTick();
    assert.equal(workbench.rows.value[0].id, 'south-only');
    assert.equal(workbench.permissionDenied.value, false);
    assert.equal(workbench.queryBusy.value, false);
    assert.equal(workbench.countBusy.value, false);
    assert.ok(workbench.historyEntries.every((entry) => entry.database === 'South:DB'));
  } finally { workbench.cleanup(); }
});

test('late distinct and aggregate results are discarded after changing the connection endpoint', { timeout: 5000 }, async () => {
  for (const [apiName, runName, response] of [
    ['distinctDocuments', 'runDistinct', { path: '$.site', values: ['old-secret'] }],
    ['aggregateDocuments', 'runAggregate', { documents: [{ secret: 'old-secret' }], count: 1 }],
  ]) {
    const pending = deferred();
    const workbench = await loadWorkbench({ [apiName]: () => pending.promise });
    try {
      workbench.props.collection = collection();
      await nextTick();
      await nextTick();
      const run = workbench[runName]();
      workbench.connection.activeBaseUrl = 'https://new-endpoint.test';
      await nextTick();
      await nextTick();
      pending.resolve(response);
      await run;
      assert.equal(JSON.stringify(workbench.latestResult.value).includes('old-secret'), false, apiName);
      assert.equal(workbench.queryBusy.value, false);
    } finally { workbench.cleanup(); }
  }
});

test('staged writes preserve the approved original target and clear before a database or connection change', { timeout: 5000 }, async () => {
  const writes = [];
  const workbench = await loadWorkbench({ insertOneDocument: async (_api, database, name, request) => { writes.push({ database, name, request }); return { inserted: 1 }; } });
  try {
    workbench.props.collection = collection();
    await nextTick();
    await nextTick();
    workbench.editId.value = 'Original:ID';
    workbench.editJson.value = '{"value":1}';
    workbench.stageInsertDocument();
    assert.equal(workbench.previewPlan.value.target, 'North:DB.Device:Profiles');
    workbench.props.targetDb = 'South:DB';
    await workbench.confirmPendingOperations();
    assert.equal(writes.length, 0);
    assert.equal(workbench.previewPlan.value, null);
    await nextTick();
    workbench.editId.value = 'Original:ID';
    workbench.editJson.value = '{"value":1}';
    workbench.stageInsertDocument();
    await workbench.confirmPendingOperations();
    assert.deepEqual(writes.map(({ database, name }) => ({ database, name })), [{ database: 'South:DB', name: 'Device:Profiles' }]);
    assert.equal(workbench.historyEntries.find((entry) => entry.kind === 'operation').database, 'South:DB');
    workbench.stageInsertDocument();
    workbench.connection.activeBaseUrl = 'https://other.test';
    assert.equal(workbench.previewPlan.value, null);
  } finally { workbench.cleanup(); }
});

test('Document import freezes its approved mode and stops future batches when database context changes', { timeout: 5000 }, async () => {
  const firstBatch = deferred();
  const batches = [];
  const workbench = await loadWorkbench({ bulkWriteDocuments: async (_api, database, name, request) => { batches.push({ database, name, request }); return firstBatch.promise; } });
  try {
    workbench.props.collection = collection();
    await nextTick();
    await nextTick();
    workbench.importText.value = JSON.stringify(Array.from({ length: 101 }, (_, index) => ({ id: `id-${index}`, document: {} })));
    workbench.stageImportDocuments();
    workbench.importMode.value = 'replace';
    const confirm = workbench.confirmPendingOperations();
    assert.equal(batches[0].request.operations[0].type, 'insertOne');
    workbench.props.targetDb = 'South:DB';
    firstBatch.resolve({ collection: 'Device:Profiles', inserted: 100 });
    await confirm;
    assert.equal(batches.length, 1);
    assert.equal(batches[0].database, 'North:DB');
    assert.equal(workbench.latestCommand.value.includes('insertMany'), false);
    const history = workbench.historyEntries.find((entry) => entry.kind === 'operation');
    assert.equal(history.database, 'North:DB');
    assert.ok(history.summary.includes('stopped after 100/101'));
  } finally { workbench.cleanup(); }
});

test('stopped import progress and per-item errors survive a same-identity schema refresh', { timeout: 5000 }, async () => {
  const firstBatch = deferred();
  const workbench = await loadWorkbench({ bulkWriteDocuments: () => firstBatch.promise });
  try {
    workbench.props.collection = collection();
    await nextTick();
    await nextTick();
    workbench.importText.value = JSON.stringify(Array.from({ length: 101 }, (_, index) => ({ id: `id-${index}`, document: {} })));
    workbench.stageImportDocuments();
    const confirm = workbench.confirmPendingOperations();
    workbench.cancelDocumentImport();
    firstBatch.resolve({ collection: 'Device:Profiles', inserted: 99, errors: [{ index: 0, id: 'id-0', code: 'duplicate_key', message: 'duplicate id', severity: 'error' }] });
    await confirm;
    workbench.props.collection = collection();
    await nextTick();
    assert.equal(workbench.importProgress.value.done, 100);
    assert.equal(workbench.importProgress.value.total, 101);
    assert.equal(workbench.importProgress.value.cancelled, true);
    assert.equal(workbench.importErrors.value[0].code, 'duplicate_key');
    assert.ok(workbench.importText.value.includes('id-100'));
  } finally { workbench.cleanup(); }
});

test('late file read failure and native-picker fallback cannot affect a new or read-only resource', { timeout: 5000 }, async () => {
  for (const change of ['database', 'readonly']) {
    const fileRead = deferred();
    const bridge = deferred();
    const workbench = await loadWorkbench({}, () => bridge.promise);
    let pickerClicks = 0;
    try {
      workbench.props.collection = collection();
      await nextTick();
      await nextTick();
      workbench.importFileInput.value = { click: () => { pickerClicks += 1; } };
      const read = workbench.onImportFileSelected({ target: { files: [{ size: 1, text: () => fileRead.promise }], value: 'file.json' } });
      const pick = workbench.pickImportFile();
      if (change === 'database') workbench.props.targetDb = 'South:DB';
      else workbench.props.readOnly = true;
      fileRead.reject(new Error('old file read failed'));
      bridge.reject(new Error('old native picker failed'));
      await Promise.all([read, pick]);
      assert.equal(workbench.errorMsg.value, '', change);
      assert.equal(pickerClicks, 0, change);
    } finally { workbench.cleanup(); }
  }
});

test('a late native bridge handshake cannot open a picker after database or read-only context changes', { timeout: 5000 }, async () => {
  for (const change of ['database', 'readonly']) {
    const bridge = deferred();
    const workbench = await loadWorkbench({}, () => bridge.promise);
    let nativePickerCalls = 0;
    try {
      workbench.props.collection = collection();
      await nextTick();
      await nextTick();
      const pick = workbench.pickImportFile();
      if (change === 'database') workbench.props.targetDb = 'South:DB';
      else workbench.props.readOnly = true;
      bridge.resolve({ manifest: { capabilities: ['dialogs.openFile'] }, openTextFile: async () => { nativePickerCalls += 1; return { canceled: true, content: null }; } });
      await pick;
      assert.equal(nativePickerCalls, 0, change);
    } finally { workbench.cleanup(); }
  }
});

test('current permission failures clear document payload and ordinary failures expose error without writes', { timeout: 5000 }, async () => {
  let failure = null;
  const workbench = await loadWorkbench({ findDocuments: async () => { if (failure) throw failure; return findResponse('secret'); } });
  try {
    workbench.props.collection = collection();
    await nextTick();
    await nextTick();
    failure = new Error('network unavailable');
    await workbench.runFind(false);
    assert.equal(workbench.documentState.value, 'error');
    failure = { response: { status: 403, data: { code: 'forbidden', message: 'Document Read required' } } };
    await workbench.runFind(false);
    assert.equal(workbench.documentState.value, 'permission');
    assert.equal(workbench.rows.value.length, 0);
    assert.equal(workbench.pendingOperations.value.length, 0);
  } finally { workbench.cleanup(); }
});
