import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const { descriptor } = parse(readFileSync(new URL('../src/components/GraphWorkbench.vue', import.meta.url), 'utf8'), { filename: 'GraphWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'graph-workbench-migration' });
const apiSource = readFileSync(new URL('../src/api/graphs.ts', import.meta.url), 'utf8');
const clientSource = readFileSync(new URL('../src/api/client.ts', import.meta.url), 'utf8');
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  const entries = Object.entries(exports); const deadline = Date.now() + 1000;
  for (let index = 0; index < entries.length && index < 1000 && Date.now() < deadline; index += 1) this.setExport(...entries[index]);
});
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await vue.nextTick(); await Promise.resolve(); await Promise.resolve(); };
const overview = (graph = 'Routes:Original', capability = true, count = 20) => ({ graph: { name: graph, storageId: 'fixture', recordFormatVersion: 1 },
  snapshotSequence: 4, vertexCount: count, edgeCount: count, labels: [], indexes: [], degreeHistogram: [], slowTraversals: [], slowTraversalSource: 'fixture',
  capabilities: { boundedVisualization: capability } });
const vertex = (id = 1, properties = []) => ({ id, elementVersion: 2, labels: [1], properties });
const visual = (id = 1) => ({ snapshotSequence: 4, truncated: false, vertices: [vertex(id)], edges: [] });
const denial = (status = 403) => ({ response: { status, data: { message: 'server-secret-body', code: 'server-secret-code' } } });
const approval = (state = 'staged', extras = {}) => ({ approvalId: 'approval-original', database: 'alpha', graph: 'Routes:Original', action: 'RepairRebuild',
  state, principal: 'fixture', occurredAtUtc: '2026-10-06T00:00:00Z', expiresAtUtc: '2026-10-06T00:10:00Z', compactOnCompletion: false, maxWorkUnits: 64, ...extras });

async function fixture({ autoReads = true, honorAbort = true, actualAxios = false } = {}) {
  let automatic = autoReads;
  const calls = []; const history = []; const notices = []; const disposers = []; const charts = []; const downloads = []; const instances = [];
  const props = vue.reactive({ targetDb: 'alpha', graph: 'Routes:Original', graphs: [{ name: 'Routes:Original', storageId: 'fixture', recordFormatVersion: 1 }],
    readOnly: false, permissionDenied: false });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  const classify = (method, url) => {
    const tail = url.split('/').at(-1);
    if (/\/vertices\//u.test(url)) return method === 'get' ? 'vertex' : method === 'delete' ? 'delete' : 'upsertVertex';
    if (/\/edges\//u.test(url)) return method === 'get' ? 'edge' : method === 'delete' ? 'delete' : 'upsertEdge';
    return tail;
  };
  const defaultData = (action) => action === 'overview' ? overview(props.graph) : action === 'visualization' ? visual()
    : action === 'audit' ? { items: [] } : action === 'vertex' ? vertex() : action === 'edge' ? { ...vertex(), sourceId: 1, targetId: 2, labelId: 1 }
    : action === 'export' ? new Blob([JSON.stringify({ ...visual(), elementCount: 1 })], { type: 'application/json' })
    : { sequence: 10, isDuplicate: false };
  const clientModule = new SourceTextModule(stripTypeScriptTypes(clientSource.replace('import.meta.env.BASE_URL', "'http://first.invalid'"), { mode: 'transform' }));
  await clientModule.link((name) => {
    assert.equal(name, 'axios');
    return synthetic({ default: { create(config) {
      return axios.create({ ...config, adapter: async (request) => {
        const action = classify(request.method, request.url);
        calls.push({ action, url: request.url, method: request.method, body: typeof request.data === 'string' ? JSON.parse(request.data) : request.data,
          signal: request.signal, params: request.params, baseUrl: request.baseURL, token: request.headers.Authorization });
        return { config: request, data: defaultData(action), status: 200, statusText: 'OK', headers: {} };
      } });
    } } });
  });
  await clientModule.evaluate({ timeout: 3000 });
  const createApiClient = (getToken) => {
    if (actualAxios) return vue.markRaw(clientModule.namespace.createApiClient(getToken));
    const client = { defaults: { baseURL: 'http://first.invalid', headers: { common: {} } } };
    const request = (method, url, body, config = {}) => {
      const action = classify(method, url);
      const call = { action, method, url, body, signal: config.signal, params: config.params, baseUrl: client.defaults.baseURL, token: getToken() };
      calls.push(call);
      return new Promise((resolve, reject) => {
        const cleanup = () => config.signal?.removeEventListener('abort', abort);
        const abort = () => { if (honorAbort) { cleanup(); reject(Object.assign(new Error('cancelled'), { code: 'ERR_CANCELED' })); } };
        call.resolve = (data) => { cleanup(); resolve({ data }); };
        call.reject = (error) => { cleanup(); reject(error); };
        config.signal?.addEventListener('abort', abort, { once: true });
        if (config.signal?.aborted && honorAbort) return abort();
        if (automatic && ['overview', 'audit'].includes(action)) call.resolve(defaultData(action));
      });
    };
    client.get = (url, config) => request('get', url, undefined, config);
    client.put = (url, body, config) => request('put', url, body, config);
    client.post = (url, body, config) => request('post', url, body, config);
    client.delete = (url, config) => request('delete', url, config?.data, config);
    return vue.markRaw(client);
  };
  const auth = vue.reactive({ state: { token: 'first' }, api: createApiClient(() => auth.state.token) });
  const initChart = (element) => {
    const chart = { element, disposed: false, getDom: () => element, setOption: (option) => charts.push(option), clear() {}, off() {}, on() {}, resize() {},
      dispose() { this.disposed = true; } };
    instances.push(chart); return chart;
  };
  const uiNames = ['NAlert', 'NButton', 'NCheckbox', 'NDataTable', 'NInput', 'NInputNumber', 'NRadioButton', 'NRadioGroup', 'NSelect', 'NTag'];
  const iconNames = ['ChartNoAxesColumnIncreasing', 'ClipboardCheck', 'Download', 'FolderOpen', 'ListTree', 'MousePointer2', 'Network', 'RefreshCw',
    'Save', 'ScrollText', 'Search', 'ShieldAlert', 'ShieldCheck', 'Tags', 'TimerReset', 'Trash2', 'Upload', 'Wrench'];
  const modules = new Map([
    ['vue', synthetic({ ...vue, onBeforeUnmount: (callback) => disposers.push(callback) })],
    ['naive-ui', synthetic({ ...Object.fromEntries(uiNames.map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'warning', 'error', 'info'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['echarts/core', synthetic({ use() {}, init: initChart })], ['echarts/charts', synthetic({ GraphChart: {} })],
    ['echarts/components', synthetic({ LegendComponent: {}, TooltipComponent: {} })], ['echarts/renderers', synthetic({ CanvasRenderer: {} })],
    ['lucide-vue-next', synthetic(Object.fromEntries(iconNames.map((name) => [name, {}])))],
    ['@/api/graphs', new SourceTextModule(stripTypeScriptTypes(apiSource, { mode: 'transform' }))],
    ['@/api/client', actualAxios ? clientModule : synthetic({ createApiClient })],
    ['@/components/WorkbenchSectionTabs.vue', synthetic({ default: {} })], ['@/components/WriteApprovalPanel.vue', synthetic({ default: {} })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })], ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (options) => options })],
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => { assert.ok(modules.has(name), `Unexpected dependency: ${name}`); return modules.get(name); });
  await module.evaluate({ timeout: 3000 });
  const previousObserver = globalThis.ResizeObserver;
  const previousDocument = globalThis.document;
  globalThis.ResizeObserver = class { observe() {} disconnect() {} };
  globalThis.document = { createElement: () => ({ click() { downloads.push({ name: this.download, url: this.href }); } }) };
  const scope = vue.effectScope();
  const component = scope.run(() => module.namespace.default.setup(props, { expose() {} }));
  const dispose = () => {
    disposers.forEach((callback) => callback()); scope.stop();
    if (previousObserver === undefined) delete globalThis.ResizeObserver; else globalThis.ResizeObserver = previousObserver;
    if (previousDocument === undefined) delete globalThis.document; else globalThis.document = previousDocument;
  };
  const latest = (action) => calls.filter((call) => call.action === action).at(-1);
  const writes = () => calls.filter((call) => call.method !== 'get');
  const stage = () => { component.editorId.value = 1; component.propertiesText.value = '[{"propertyId":1,"value":{"kind":5,"string":"write-secret"}}]'; component.stageElementSave(); };
  await settle(); await settle(); await settle();
  return { component, props, auth, connections, calls, history, notices, charts, instances, downloads, latest, writes, stage, dispose, pauseReads: () => { automatic = false; } };
}

test('overview capability false or missing suppresses visualization requests and exposes a safe unavailable state', { timeout: 5000 }, async () => {
  for (const capability of [false, undefined]) {
    const f = await fixture({ autoReads: false });
    try {
      assert.ok(!f.latest('visualization'));
      const response = overview(f.props.graph, capability);
      if (capability === undefined) delete response.capabilities.boundedVisualization;
      f.latest('overview').resolve(response); await settle();
      await f.component.loadVisualization(); assert.ok(!f.latest('visualization')); assert.equal(f.component.canVisualize.value, false);
      assert.equal(f.component.visualization.value, null);
    } finally { f.dispose(); }
  }
});

test('client total element budget slices before render and retains only edges with kept endpoints', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.visualizationLimit.value = 10;
    f.component.chartElement.value = {};
    const run = f.component.loadVisualization(); assert.equal(f.latest('visualization').params.limit, 10);
    const vertices = Array.from({ length: 8 }, (_, index) => vertex(index + 1));
    const edges = [{ id: 1, sourceId: 1, targetId: 2, labelId: 1, properties: [] }, { id: 2, sourceId: 3, targetId: 4, labelId: 1, properties: [] },
      { id: 3, sourceId: 999, targetId: 2, labelId: 1, properties: [] }];
    f.latest('visualization').resolve({ snapshotSequence: 9, vertices, edges, truncated: false }); await run;
    assert.equal(f.component.visualization.value.vertices.length + f.component.visualization.value.edges.length, 10);
    assert.equal(f.component.visualization.value.truncated, true);
    assert.equal(f.charts.at(-1).series[0].data.length + f.charts.at(-1).series[0].edges.length, 10);
    assert.ok(f.component.visualization.value.edges.every((edge) => vertices.some((item) => item.id === edge.sourceId) && vertices.some((item) => item.id === edge.targetId)));
    f.component.visualizationLimit.value = 5000; const bounded = f.component.loadVisualization();
    assert.equal(f.latest('visualization').params.limit, 1000); f.latest('visualization').resolve(visual()); await bounded;
  } finally { f.dispose(); }
});

test('property Inspector caps 32 items and 4096 characters while full selected element editing remains intact', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const properties = Array.from({ length: 40 }, (_, index) => ({ propertyId: index + 1, value: { kind: 5, string: index === 0 ? 'L'.repeat(8192) + 'tail-secret' : `Property:${index}` } }));
    f.latest('visualization').resolve({ ...visual(), vertices: [vertex(1, properties)] }); await settle();
    f.component.selectedElement.value = { kind: 'vertex', data: f.component.visualization.value.vertices[0] };
    const text = f.component.formatProperties(properties); assert.ok(text.length <= 4096); assert.ok(!text.includes('tail-secret')); assert.ok(!text.includes('Property:39'));
    f.component.editSelectedElement(); assert.equal(JSON.parse(f.component.propertiesText.value).length, 40); assert.ok(f.component.propertiesText.value.includes('tail-secret'));
    assert.equal(f.component.graphState.value, 'longContent');
  } finally { f.dispose(); }
});

test('overview, visualization, element, audit, export and write 401/403 lock and clear all payloads and drafts', { timeout: 5000 }, async () => {
  for (const action of ['overview', 'visualization', 'vertex', 'audit', 'export', 'upsertVertex']) {
    for (const status of [401, 403]) {
      const f = await fixture({ autoReads: action !== 'overview', honorAbort: false });
      try {
        f.component.importText.value = 'import-secret'; f.component.importFileName.value = 'secret-file'; f.component.rejectReason.value = 'reason-secret';
        let run;
        if (action === 'vertex') { f.component.editorId.value = 1; run = f.component.loadElement(); }
        if (action === 'audit') { f.pauseReads(); run = f.component.loadAudit(); }
        if (action === 'export') run = f.component.exportGraph();
        if (action === 'upsertVertex') { f.stage(); run = f.component.confirmApproval(); }
        f.latest(action).reject(denial(status)); if (run) await run; await settle(); await settle();
        assert.equal(f.component.graphState.value, 'permission'); assert.equal(f.component.overview.value, null); assert.equal(f.component.visualization.value, null);
        assert.equal(f.component.selectedElement.value, null); assert.equal(f.component.pendingAction.value, null); assert.equal(f.component.stagedApproval.value, null);
        assert.equal(f.component.propertiesText.value, '[]'); assert.equal(f.component.importText.value, ''); assert.equal(f.component.importFileName.value, '');
        assert.equal(f.component.audit.value.length, 0); assert.equal(f.component.rejectReason.value, '');
        const count = f.calls.length; await f.component.refreshAll(); await f.component.loadVisualization(); await f.component.loadAudit();
        assert.equal(f.calls.length, count); assert.ok(!JSON.stringify(f.history).includes('server-secret'));
      } finally { f.dispose(); }
    }
  }
});

test('permission survives same-identity auth refresh and empty database/Graph/profile/endpoint round trips', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.latest('visualization').reject(denial()); await settle(); const count = f.calls.length;
    f.auth.state = { token: 'first' }; f.props.graph = ''; f.props.targetDb = ''; f.connections.activeProfileId = ''; f.connections.activeBaseUrl = '';
    f.props.graph = 'Routes:Original'; f.props.targetDb = 'alpha'; f.connections.activeProfileId = 'first'; f.connections.activeBaseUrl = 'http://first.invalid'; await settle();
    assert.equal(f.component.permissionDenied.value, true); assert.equal(f.calls.length, count);
    f.props.targetDb = 'beta'; await settle(); await settle(); assert.equal(f.component.permissionDenied.value, false); assert.ok(f.calls.length > count);
  } finally { f.dispose(); }
});

test('same-name cross-database and synchronous authentication ABA suppress late visualization responses', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    const stale = f.latest('visualization'); f.props.targetDb = 'beta'; await settle(); await settle();
    f.latest('visualization').resolve(visual(20)); await settle(); stale.resolve(visual(1)); await settle();
    assert.equal(f.component.visualization.value.vertices[0].id, 20);
    const run = f.component.loadVisualization(); const aba = f.latest('visualization');
    f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; aba.resolve(visual(999)); await run;
    assert.ok(!f.component.visualization.value?.vertices.some((item) => item.id === 999));
  } finally { f.dispose(); }
});

test('new visualization and editor ID ABA retain only current read ownership', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    const old = f.latest('visualization'); const newer = f.component.loadVisualization(); f.latest('visualization').resolve(visual(10)); await newer;
    old.resolve(visual(1)); await settle(); assert.equal(f.component.visualization.value.vertices[0].id, 10);
    f.component.editorId.value = 1; const run = f.component.loadElement(); const element = f.latest('vertex');
    f.component.editorId.value = 2; f.component.editorId.value = 1; element.resolve(vertex(1, [{ propertyId: 1, value: { kind: 5, string: 'stale-editor-secret' } }])); await run;
    assert.ok(!f.component.propertiesText.value.includes('stale-editor-secret'));
  } finally { f.dispose(); }
});

test('unmount aborts owned requests and prevents late graph data from reappearing', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false }); const pending = f.latest('visualization'); f.dispose();
  assert.equal(pending.signal.aborted, true); pending.resolve(visual(999)); await settle(); assert.equal(f.component.visualization.value, null);
});

test('readonly gates every programmatic write/file/approval path while keeping element reading and export', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage(); f.props.readOnly = true; f.component.editorId.value = 1; f.component.editorVersion.value = 2; f.component.importText.value = JSON.stringify({ vertices: [vertex()], edges: [] });
    f.component.stagedApproval.value = approval();
    f.component.stageElementSave(); f.component.stageElementDelete(); f.component.stageImport(); f.component.stageMaintenanceRequest(); f.component.stageApprovalDecision('approve');
    await f.component.confirmApproval(); await f.component.rejectStagedApproval();
    const input = { files: [{ text: async () => 'file-secret', name: 'fixture.json' }], value: 'selected' }; await f.component.readImportFile({ target: input });
    assert.equal(f.writes().length, 0); assert.equal(f.component.pendingAction.value, null); assert.equal(input.value, ''); assert.equal(f.component.graphState.value, 'readonly');
    const read = f.component.loadElement(); f.latest('vertex').resolve(vertex()); await read;
    const exported = f.component.exportGraph(); f.latest('export').resolve(new Blob(['{"vertices":[],"edges":[],"truncated":false}'])); await exported;
    assert.equal(f.downloads.length, 1);
  } finally { f.dispose(); }
});

test('missing, invalid sequence or duplicate mutation fields consume approval once and stay unknown', { timeout: 5000 }, async () => {
  for (const terminal of [null, { sequence: 10 }, { sequence: -1, isDuplicate: false }, { sequence: Number.MAX_SAFE_INTEGER + 1, isDuplicate: false }, 'transport']) {
    const f = await fixture();
    try {
      f.stage(); const run = f.component.confirmApproval(); assert.equal(f.component.pendingAction.value, null);
      if (terminal === 'transport') f.latest('upsertVertex').reject(new Error('transport-secret')); else f.latest('upsertVertex').resolve(terminal);
      await run; assert.equal(f.history.at(-1).status, 'unknown'); assert.ok(!JSON.stringify(f.history).includes('transport-secret'));
      await f.component.confirmApproval(); assert.equal(f.writes().length, 1);
    } finally { f.dispose(); }
  }
});

test('proved mutation success remains success when the follow-up overview fails or denies access', { timeout: 5000 }, async () => {
  for (const error of [new Error('refresh-secret'), denial()]) {
    const f = await fixture();
    try {
      f.stage(); f.pauseReads(); const run = f.component.confirmApproval(); f.latest('upsertVertex').resolve({ sequence: 10, isDuplicate: false }); await run;
      f.latest('overview').reject(error); await settle(); await settle();
      assert.equal(f.history.find((entry) => entry.title === 'Graph 元素 Upsert').status, 'success');
      assert.ok(!JSON.stringify(f.history).includes('refresh-secret'));
    } finally { f.dispose(); }
  }
});

test('import validates actual counts and retains original database/Graph spelling and frozen input', { timeout: 5000 }, async () => {
  for (const matches of [false, true]) {
    const f = await fixture();
    try {
      f.props.targetDb = 'North:DB'; f.props.graph = ' Routes:Original '; await settle(); await settle();
      f.component.importText.value = JSON.stringify({ vertices: [vertex()], edges: [] }); f.component.stageImport();
      const run = f.component.confirmApproval(); const request = f.latest('import');
      assert.match(request.url, /North%3ADB\/graphs\/%20Routes%3AOriginal%20\/import$/u);
      f.component.importText.value = 'changed after dispatch'; assert.equal(request.body.vertices.length, 1);
      request.resolve({ sequence: 8, isDuplicate: false, vertexCount: matches ? 1 : 2, edgeCount: 0 }); await run;
      const entry = f.history.find((item) => item.title === '导入 Graph JSON'); assert.equal(entry.status, matches ? 'success' : 'unknown');
      assert.equal(entry.database, 'North:DB'); assert.equal(entry.target, ' Routes:Original ');
    } finally { f.dispose(); }
  }
});

test('maintenance stage is dry-run and wrong target or state cannot reinstate server approval', { timeout: 5000 }, async () => {
  for (const terminal of [approval(), approval('staged', { graph: 'wrong' }), approval('completed')]) {
    const f = await fixture();
    try {
      f.component.stageMaintenanceRequest(); const run = f.component.confirmApproval(); f.latest('stage').resolve(terminal); await run;
      const valid = terminal.graph === f.props.graph && terminal.state === 'staged';
      assert.equal(f.history.find((entry) => entry.title === '暂存 Graph 维护').status, valid ? 'dry-run' : 'unknown');
      assert.equal(f.component.stagedApproval.value?.state ?? null, valid ? 'staged' : null);
      await f.component.confirmApproval(); assert.equal(f.writes().length, 1);
    } finally { f.dispose(); }
  }
});

test('approve only completed plus matching isComplete terminal counts as success; paused/applying remain incomplete', { timeout: 5000 }, async () => {
  for (const [state, complete, expected] of [['paused', false, 'unknown'], ['applying', false, 'unknown'], ['completed', false, 'unknown'], ['completed', true, 'success']]) {
    const f = await fixture();
    try {
      f.component.stagedApproval.value = approval(); f.component.stageApprovalDecision('approve'); const run = f.component.confirmApproval();
      assert.equal(f.component.stagedApproval.value, null);
      f.latest('approve').resolve(approval(state, { result: { action: 'RepairRebuild', isComplete: complete, sequence: 10 } })); await run;
      assert.equal(f.history.find((entry) => entry.title === '批准 Graph 维护').status, expected);
      await f.component.confirmApproval(); f.component.stageApprovalDecision('approve'); await f.component.confirmApproval(); assert.equal(f.writes().length, 1);
    } finally { f.dispose(); }
  }
});

test('reject validates original approval identity and consumes it before dispatch without replay', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.stagedApproval.value = approval(); const run = f.component.rejectStagedApproval(); assert.equal(f.component.stagedApproval.value, null);
    f.latest('reject').resolve(approval('rejected', { approvalId: 'wrong-id' })); await run;
    assert.equal(f.history.find((entry) => entry.title === '拒绝 Graph 维护').status, 'unknown');
    await f.component.rejectStagedApproval(); assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('late dispatched mutation after database ABA records original identity as unknown without refreshing new data', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.stage(); const run = f.component.confirmApproval(); const write = f.latest('upsertVertex');
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; write.resolve({ sequence: 10, isDuplicate: false }); await run;
    const entry = f.history.find((item) => item.title === 'Graph 元素 Upsert'); assert.equal(entry.status, 'unknown'); assert.equal(entry.database, 'alpha');
    assert.equal(f.component.pendingAction.value, null); await f.component.confirmApproval(); assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('production Axios cancels an element read before adapter dispatch on synchronous database ABA', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    f.component.editorId.value = 80;
    const run = f.component.loadElement(); f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; await run; await settle();
    assert.ok(!f.calls.some((call) => call.action === 'vertex' && call.url.endsWith('/vertices/80')));
  } finally { f.dispose(); }
});

test('unsafe numeric ID, version or edge endpoint cannot enter an editor, dispatch a read or obtain write approval', { timeout: 5000 }, async () => {
  const f = await fixture(); const unsafe = Number.MAX_SAFE_INTEGER + 1;
  try {
    const reads = f.calls.length;
    f.component.editorId.value = unsafe; await f.component.loadElement();
    f.component.editorId.value = 1; f.component.editorVersion.value = unsafe; await f.component.loadElement();
    assert.equal(f.calls.length, reads); assert.equal(f.component.editorId.value, null);
    for (const data of [vertex(unsafe), { ...vertex(), elementVersion: unsafe },
      { ...vertex(), sourceId: unsafe, targetId: 2, labelId: 1 }, { ...vertex(), sourceId: 1, targetId: unsafe, labelId: 1 }]) {
      f.component.selectedElement.value = { kind: 'sourceId' in data ? 'edge' : 'vertex', data };
      f.component.editSelectedElement(); assert.equal(f.component.editorId.value, null); assert.equal(f.component.propertiesText.value, '[]');
    }
    for (const invalid of ['id', 'version', 'source', 'target', 'label']) {
      f.component.editorKind.value = invalid === 'id' || invalid === 'version' ? 'vertex' : 'edge';
      f.component.editorId.value = invalid === 'id' ? unsafe : 1; f.component.editorVersion.value = invalid === 'version' ? unsafe : 2;
      f.component.sourceId.value = invalid === 'source' ? unsafe : 1; f.component.targetId.value = invalid === 'target' ? unsafe : 2;
      f.component.edgeLabelId.value = invalid === 'label' ? unsafe : 1;
      f.component.stageElementSave(); assert.equal(f.component.pendingAction.value, null); await f.component.confirmApproval();
    }
    f.component.editorId.value = 1; f.component.editorVersion.value = unsafe; f.component.stageElementDelete(); await f.component.confirmApproval();
    f.component.editorKind.value = 'vertex'; f.component.editorId.value = 1; f.component.editorVersion.value = 0;
    const load = f.component.loadElement(); f.latest('vertex').resolve({ ...vertex(), elementVersion: unsafe }); await load;
    assert.equal(f.component.editorId.value, null); assert.match(f.component.errorMsg.value, /安全整数/u);
    f.component.editorId.value = 1; f.component.labelsText.value = JSON.stringify([unsafe]); f.component.stageElementSave();
    assert.equal(f.component.pendingAction.value, null);
    f.component.importText.value = JSON.stringify({ vertices: [{ ...vertex(), id: unsafe }], edges: [] }); f.component.stageImport();
    assert.equal(f.component.pendingAction.value, null);
    f.component.importText.value = JSON.stringify({ vertices: [], edges: [{ ...vertex(), sourceId: unsafe, targetId: 2, labelId: 1 }] }); f.component.stageImport();
    assert.equal(f.component.pendingAction.value, null); assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});

test('production Axios authentication ABA cancels an approved mutation before adapter dispatch', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    f.stage(); const run = f.component.confirmApproval(); f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; await run;
    assert.equal(f.writes().length, 0); assert.equal(f.history.find((entry) => entry.title === 'Graph 元素 Upsert').status, 'unknown');
  } finally { f.dispose(); }
});

test('canvas instances bind to the current element after section changes, capability transitions and safe identity recovery', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const first = {}; f.component.chartElement.value = first; f.latest('visualization').resolve(visual()); await settle(); await settle();
    assert.equal(f.instances.at(-1).getDom(), f.component.chartElement.value);
    const original = f.instances.at(-1); f.component.activeSection.value = 'schema'; await settle(); assert.equal(original.disposed, true);
    const second = {}; f.component.chartElement.value = second; f.component.activeSection.value = 'canvas'; await settle(); await settle();
    assert.equal(f.instances.at(-1).getDom(), f.component.chartElement.value); assert.notEqual(f.instances.at(-1), original);
    f.pauseReads(); const disabled = f.component.refreshAll(); f.latest('overview').resolve(overview(f.props.graph, false)); await disabled;
    assert.equal(f.instances.at(-1).disposed, true); assert.equal(f.component.visualization.value, null);
    const enabled = f.component.refreshAll(); f.latest('overview').resolve(overview()); await settle();
    f.component.chartElement.value = {}; f.latest('visualization').resolve(visual(2)); await enabled;
    assert.equal(f.instances.at(-1).getDom(), f.component.chartElement.value); assert.equal(f.instances.at(-1).disposed, false);
    const previous = f.instances.at(-1); f.props.permissionDenied = true; assert.equal(previous.disposed, true);
    f.props.permissionDenied = false; await settle(); f.latest('overview').resolve(overview()); await settle();
    f.component.chartElement.value = {}; f.latest('visualization').resolve(visual(3)); await settle(); await settle();
    assert.equal(f.instances.at(-1).getDom(), f.component.chartElement.value); assert.notEqual(f.instances.at(-1), previous);
  } finally { f.dispose(); }
});
