import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const source = readFileSync(new URL('../src/components/GraphWorkbench.vue', import.meta.url), 'utf8');
const { descriptor } = parse(source, { filename: 'GraphWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'graph-workflow-regression' });
const apiNames = [
  'approveGraphMaintenance', 'deleteGraphElement', 'downloadGraphExport', 'fetchGraphEdge',
  'fetchGraphMaintenanceAudit', 'fetchGraphOperationsOverview', 'fetchGraphVertex',
  'fetchGraphVisualization', 'importGraphJson', 'rejectGraphMaintenance', 'stageGraphMaintenance',
  'upsertGraphEdge', 'upsertGraphVertex',
];
const iconNames = ['ChartNoAxesColumnIncreasing', 'ClipboardCheck', 'Download', 'FolderOpen',
  'ListTree', 'MousePointer2', 'Network', 'RefreshCw', 'Save', 'ScrollText', 'Search', 'ShieldAlert',
  'ShieldCheck', 'Tags', 'TimerReset', 'Trash2', 'Upload', 'Wrench'];
const uiNames = ['NAlert', 'NButton', 'NCheckbox', 'NDataTable', 'NInput', 'NInputNumber',
  'NRadioButton', 'NRadioGroup', 'NSelect', 'NTag'];
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
});

async function fixture() {
  const calls = [];
  const history = [];
  const notices = [];
  const disposers = [];
  const props = vue.reactive({ targetDb: 'alpha', graph: 'graph_a', graphs: [] });
  const auth = vue.reactive({ api: vue.markRaw({ defaults: { baseURL: 'http://first.invalid' } }), state: { token: 'first' } });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  const graphApi = Object.fromEntries(apiNames.map((name) => [name, (...args) => new Promise((resolve, reject) => {
    calls.push({ name, args, resolve, reject });
  })]));
  const modules = new Map([
    ['vue', synthetic({ ...vue, onBeforeUnmount: (callback) => disposers.push(callback) })],
    ['naive-ui', synthetic({ ...Object.fromEntries(uiNames.map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'warning', 'error', 'info'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['echarts/core', synthetic({ use() {} })],
    ['echarts/charts', synthetic({ GraphChart: {} })],
    ['echarts/components', synthetic({ LegendComponent: {}, TooltipComponent: {} })],
    ['echarts/renderers', synthetic({ CanvasRenderer: {} })],
    ['lucide-vue-next', synthetic(Object.fromEntries(iconNames.map((name) => [name, {}])))],
    ['@/api/graphs', synthetic(graphApi)],
    ['@/components/WorkbenchSectionTabs.vue', synthetic({ default: {} })],
    ['@/components/WriteApprovalPanel.vue', synthetic({ default: {} })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })],
    ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (options) => options })],
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => {
    assert.ok(modules.has(name), `Unexpected dependency: ${name}`);
    return modules.get(name);
  });
  await module.evaluate({ timeout: 3000 });
  const scope = vue.effectScope();
  const component = scope.run(() => module.namespace.default.setup(props, { expose() {} }));
  const dispose = () => { disposers.forEach((callback) => callback()); scope.stop(); };
  const latest = (name) => calls.filter((call) => call.name === name).at(-1);
  return { props, auth, connections, component, calls, history, notices, dispose, latest };
}

const visual = (id) => ({ snapshotSequence: id, vertices: [{ id, labels: [], properties: [] }], edges: [], truncated: false });
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await vue.nextTick(); };

test('Graph approvals expire on target, connection, credentials and editor changes', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.editorId.value = 1;
    f.component.stageElementSave();
    assert.ok(f.component.pendingAction.value);
    f.component.editorId.value = 2;
    await f.component.confirmApproval();
    assert.equal(f.component.pendingAction.value, null);
    f.component.stageElementSave();
    f.props.graph = 'graph_b';
    await f.component.confirmApproval();
    assert.equal(f.component.pendingAction.value, null);
    f.component.editorId.value = 2;
    f.component.stageElementSave();
    f.connections.activeProfileId = 'second';
    await f.component.confirmApproval();
    f.component.editorId.value = 2;
    f.component.stageElementSave();
    f.auth.state.token = 'second';
    await f.component.confirmApproval();
    assert.equal(f.calls.filter((call) => call.name.startsWith('upsert')).length, 0);
  } finally { f.dispose(); }
});

test('A mutable API base URL cannot reuse an old approval even without a reactive notification', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.editorId.value = 1;
    f.component.stageElementSave();
    f.auth.api.defaults.baseURL = 'http://other.invalid';
    await f.component.confirmApproval();
    assert.equal(f.component.pendingAction.value, null);
    assert.equal(f.calls.filter((call) => call.name.startsWith('upsert')).length, 0);
  } finally { f.dispose(); }
});

test('Late graph A responses and failures cannot overwrite graph B data, loading or errors', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const firstOverview = f.latest('fetchGraphOperationsOverview');
    const firstVisual = f.latest('fetchGraphVisualization');
    f.props.graph = 'graph_b';
    await settle();
    f.latest('fetchGraphOperationsOverview').resolve({ vertexCount: 22 });
    f.latest('fetchGraphVisualization').resolve(visual(22));
    await settle();
    firstOverview.reject(new Error('obsolete graph failure'));
    firstVisual.resolve(visual(1));
    await settle();
    assert.equal(f.component.overview.value.vertexCount, 22);
    assert.equal(f.component.visualization.value.vertices[0].id, 22);
    assert.equal(f.component.errorMsg.value, '');
    assert.equal(f.component.busy.value, false);
    assert.equal(f.history.length, 0);
  } finally { f.dispose(); }
});

test('A newer same-graph visualization owns the result while an older refresh still completes overview', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const firstOverview = f.latest('fetchGraphOperationsOverview');
    const firstVisual = f.latest('fetchGraphVisualization');
    const newer = f.component.loadVisualization();
    f.latest('fetchGraphVisualization').resolve(visual(9));
    await newer;
    firstOverview.resolve({ vertexCount: 5 });
    firstVisual.resolve(visual(5));
    await settle();
    assert.equal(f.component.overview.value.vertexCount, 5);
    assert.equal(f.component.visualization.value.vertices[0].id, 9);
  } finally { f.dispose(); }
});

test('A completed old-target write retains its history identity without refreshing the new graph', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.editorId.value = 1;
    f.component.stageElementSave();
    const running = f.component.confirmApproval();
    const write = f.latest('upsertGraphVertex');
    assert.equal(write.args[1], 'alpha');
    assert.equal(write.args[2], 'graph_a');
    f.props.graph = 'graph_b';
    await settle();
    const beforeCompletion = f.calls.length;
    write.resolve({ sequence: 8 });
    await running;
    assert.equal(f.calls.length, beforeCompletion);
    assert.equal(f.history[0].target, 'graph_a');
    assert.equal(f.history[0].database, 'alpha');
    assert.equal(f.notices.length, 0);
    assert.equal(f.component.writeBusy.value, false);
  } finally { f.dispose(); }
});

test('Unmounting prevents late responses from repopulating graph state', { timeout: 5000 }, async () => {
  const f = await fixture();
  f.dispose();
  f.latest('fetchGraphOperationsOverview').resolve({ vertexCount: 5 });
  f.latest('fetchGraphVisualization').resolve(visual(5));
  await settle();
  assert.equal(f.component.overview.value, null);
  assert.equal(f.component.visualization.value, null);
});

test('A refresh superseding a visualization clears the loading flag and retains only its response', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const oldLoad = f.component.loadVisualization();
    const oldVisual = f.latest('fetchGraphVisualization');
    const refresh = f.component.refreshAll();
    f.latest('fetchGraphOperationsOverview').resolve({ vertexCount: 12 });
    f.latest('fetchGraphVisualization').resolve(visual(12));
    await refresh;
    assert.equal(f.component.visualizationBusy.value, false);
    oldVisual.resolve(visual(2));
    await oldLoad;
    assert.equal(f.component.visualization.value.vertices[0].id, 12);
  } finally { f.dispose(); }
});
