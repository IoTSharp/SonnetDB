import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const source = readFileSync(new URL('../src/views/RagManagementView.vue', import.meta.url), 'utf8');
const { descriptor } = parse(source, { filename: 'RagManagementView.vue' });
const compiled = compileScript(descriptor, { id: 'rag-management-regression' });
const apiSource = readFileSync(new URL('../src/api/ragManagement.ts', import.meta.url), 'utf8');
const approvalSource = readFileSync(new URL('../src/utils/writeApproval.ts', import.meta.url), 'utf8');
const uiNames = ['NAlert', 'NAutoComplete', 'NButton', 'NCard', 'NDataTable', 'NDatePicker', 'NEmpty', 'NInput', 'NInputNumber', 'NSelect', 'NSpace', 'NText'];
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  const entries = Object.entries(exports);
  const deadline = Date.now() + 1000;
  for (let index = 0; index < entries.length && index < 1000 && Date.now() < deadline; index += 1) this.setExport(...entries[index]);
});
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await vue.nextTick(); };
const profile = { id: 'profile-v1', provider: 'configured', model: 'model-v1', revision: '1', dimensions: 3, normalization: 'L2', metric: 'Cosine' };
const pending = { generationId: 'pending-42', profileId: profile.id, expectedRevision: 7, contents: 2, chunks: 5 };
const status = (stream = 'copilot-docs', task = null) => ({ stream, activeRevision: 7, activeProfileId: profile.id, activeContents: 2, activeChunks: 5, pending: task });
const successful = (operation) => ({ operation, stream: 'copilot-docs', status: 'completed', revision: 8, removedRevisions: [], deferredRevisions: [] });

async function fixture({ autoReads = true, honorAbort = true, task = null } = {}) {
  const calls = [];
  const disposers = [];
  const connections = vue.reactive({ activeDatabase: 'alpha', activeProfileId: 'first', activeBaseUrl: 'http://first.invalid' });
  const createApiClient = (getToken) => {
    const client = {
      defaults: { baseURL: 'http://first.invalid' },
      get(url, config = {}) { return request('GET', url, undefined, config); },
      post(url, body, config = {}) { return request('POST', url, body, config); },
    };
    function request(method, url, body, config) {
      const action = url.split('/').at(-1);
      const call = { method, action, url, body, config, signal: config.signal, token: getToken(), baseUrl: client.defaults.baseURL };
      calls.push(call);
      return new Promise((resolve, reject) => {
        const cleanup = () => config.signal?.removeEventListener('abort', abort);
        const abort = () => { if (honorAbort) { cleanup(); reject(Object.assign(new Error('Cancelled'), { code: 'ERR_CANCELED' })); } };
        call.resolve = (data) => { cleanup(); resolve({ data }); };
        call.reject = (error) => { cleanup(); reject(error); };
        config.signal?.addEventListener('abort', abort, { once: true });
        if (config.signal?.aborted && honorAbort) return abort();
        if (!autoReads || method !== 'GET') return;
        if (url === '/v1/db') call.resolve({ databases: ['alpha', 'beta'] });
        if (action === 'status') call.resolve(status(config.params.stream, task));
        if (action === 'profiles') call.resolve({ profiles: [profile, { ...profile, id: 'profile-v2', model: 'model-v2' }] });
        if (action === 'audit') call.resolve({ entries: [], continuationToken: 'next-page' });
      });
    }
    return vue.markRaw(client);
  };
  const auth = vue.reactive({ state: { token: 'first' }, api: createApiClient(() => auth.state?.token ?? null) });
  const modules = new Map([
    ['vue', synthetic({ ...vue, onBeforeUnmount: (callback) => disposers.push(callback) })],
    ['naive-ui', synthetic(Object.fromEntries(uiNames.map((name) => [name, {}])))],
    ['@/api/client', synthetic({ createApiClient })],
    ['@/api/ragManagement', new SourceTextModule(stripTypeScriptTypes(apiSource, { mode: 'transform' }))],
    ['@/utils/writeApproval', new SourceTextModule(stripTypeScriptTypes(approvalSource, { mode: 'transform' }))],
    ['@/components/WriteApprovalPanel.vue', synthetic({ default: {} })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })],
    ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => { assert.ok(modules.has(name), `Unexpected dependency: ${name}`); return modules.get(name); });
  await module.evaluate({ timeout: 3000 });
  const scope = vue.effectScope();
  const component = scope.run(() => module.namespace.default.setup({}, { expose() {} }));
  const latest = (action) => calls.filter((call) => call.action === action).at(-1);
  const writes = () => calls.filter((call) => call.method === 'POST');
  const dispose = () => { disposers.forEach((callback) => callback()); scope.stop(); };
  await settle();
  return { component, auth, connections, calls, latest, writes, dispose };
}

test('RAG management loads only configured profiles and stages before sending a revision-bound rebuild', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    assert.equal(f.writes().length, 0);
    await f.component.refresh();
    f.component.selectedProfileId.value = 'profile-v2';
    f.component.stageOperation('rebuild');
    assert.equal(f.writes().length, 0);
    assert.equal(f.component.approvalPlan.value.target, 'alpha / copilot-docs');
    const running = f.component.confirmOperation();
    const call = f.latest('rebuild');
    assert.deepEqual(call.body, { stream: 'copilot-docs', expectedRevision: 7, profileId: 'profile-v2' });
    assert.equal(call.url, '/v1/db/alpha/semantic/rag/rebuild');
    assert.equal(call.config.timeout, 120_000);
    call.resolve(successful('rebuild'));
    await running;
    assert.equal(f.component.busy.value, false);
    assert.match(f.component.infoMsg.value, /revision 8/);
  } finally { f.dispose(); }
});

test('Hidden system databases can be entered, survive list refresh and still require write approval', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.database.value = ' __copilot__ ';
    await f.component.loadDatabases();
    assert.equal(f.component.database.value, ' __copilot__ ');
    assert.deepEqual(f.component.databases.value, ['alpha', 'beta']);
    await f.component.refresh();
    assert.equal(f.latest('status').url, '/v1/db/__copilot__/semantic/rag/status');
    f.component.stageOperation('rebuild');
    assert.equal(f.component.approvalPlan.value.target, '__copilot__ / copilot-docs');
    assert.equal(f.writes().length, 0);
    f.component.database.value = ' '.repeat(257);
    assert.equal(f.component.validTarget.value, false);
    f.component.database.value = 'a'.repeat(257);
    assert.equal(f.component.validTarget.value, false);
    assert.match(descriptor.template.content, /n-auto-complete[^\n]*v-model:value="database"/);
  } finally { f.dispose(); }
});

test('RAG resume binds the frozen pending generation and rejects an approval for a replaced task', { timeout: 5000 }, async () => {
  const f = await fixture({ task: { ...pending } });
  try {
    await f.component.refresh();
    assert.equal(f.component.canRebuild.value, false);
    f.component.stageOperation('resume');
    f.component.status.value.pending.generationId = 'replacement-task';
    await f.component.confirmOperation();
    assert.equal(f.writes().length, 0);
    f.component.stageOperation('resume');
    const running = f.component.confirmOperation();
    const call = f.latest('resume');
    assert.equal(call.body.pendingGenerationId, 'replacement-task');
    assert.equal(call.body.expectedRevision, 7);
    assert.equal(call.body.profileId, profile.id);
    call.resolve(successful('resume'));
    await running;
  } finally { f.dispose(); }
});

test('RAG discard and cleanup require dangerous previews with explicit target and cleanup cutoff', { timeout: 5000 }, async () => {
  const f = await fixture({ task: { ...pending } });
  try {
    await f.component.refresh();
    f.component.stageOperation('discard');
    assert.equal(f.component.approvalPlan.value.dangerous, true);
    assert.equal(f.component.pendingOperation.value.request.pendingGenerationId, pending.generationId);
    assert.equal(f.writes().length, 0);
    f.component.clearApproval();
    f.component.retiredBefore.value = Date.parse('2026-09-01T00:00:00Z');
    f.component.maxGenerations.value = 3;
    f.component.stageOperation('cleanup');
    assert.equal(f.component.approvalPlan.value.dangerous, true);
    const running = f.component.confirmOperation();
    const call = f.latest('cleanup');
    assert.deepEqual(call.body, { stream: 'copilot-docs', expectedRevision: 7, retiredBeforeUtc: '2026-09-01T00:00:00.000Z', maxGenerations: 3 });
    call.resolve({ ...successful('cleanup'), removedRevisions: [1, 2], deferredRevisions: [3] });
    await running;
    assert.match(f.component.infoMsg.value, /已清理 2 个，租约延后 1 个/);
  } finally { f.dispose(); }
});

test('RAG approvals expire when database, stream, credentials or raw client endpoint changes', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    await f.component.refresh(); f.component.stageOperation('rebuild');
    f.component.database.value = 'beta'; await f.component.confirmOperation();
    await f.component.refresh(); f.component.stageOperation('rebuild');
    f.component.streamInput.value = 'other'; await f.component.confirmOperation();
    await f.component.refresh(); f.component.stageOperation('rebuild');
    f.auth.state.token = 'second'; await f.component.confirmOperation();
    await f.component.refresh(); f.component.stageOperation('rebuild');
    f.auth.api.defaults.baseURL = 'http://changed.invalid'; await f.component.confirmOperation();
    assert.equal(f.writes().length, 0);
    assert.equal(f.component.pendingOperation.value, null);
  } finally { f.dispose(); }
});

test('Late responses from the previous database cannot replace new state or clear loading', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false, honorAbort: false });
  try {
    f.latest('db').resolve({ databases: ['alpha', 'beta'] }); await settle();
    const oldRead = f.component.refresh();
    const old = { status: f.latest('status'), profiles: f.latest('profiles'), audit: f.latest('audit') };
    f.component.database.value = 'beta';
    const newRead = f.component.refresh();
    assert.equal(old.status.signal.aborted, true);
    old.status.reject(new Error('old request failed')); old.profiles.resolve({ profiles: [] }); old.audit.resolve({ entries: [] });
    await oldRead;
    assert.equal(f.component.loading.value, true);
    assert.equal(f.component.errorMsg.value, '');
    f.latest('status').resolve({ ...status(), activeRevision: 11 });
    f.latest('profiles').resolve({ profiles: [profile] }); f.latest('audit').resolve({ entries: [] });
    await newRead;
    assert.equal(f.component.status.value.activeRevision, 11);
    assert.equal(f.component.loading.value, false);
  } finally { f.dispose(); }
});

test('A busy management write cannot be sent twice and context switches cancel its request', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    await f.component.refresh(); f.component.stageOperation('rebuild');
    const running = f.component.confirmOperation();
    await f.component.confirmOperation(); f.component.stageOperation('cleanup');
    assert.equal(f.writes().length, 1);
    const call = f.latest('rebuild');
    assert.equal(call.token, 'first'); assert.equal(call.baseUrl, 'http://first.invalid');
    f.component.database.value = 'beta'; await f.component.refresh();
    assert.equal(call.signal.aborted, true);
    call.resolve(successful('rebuild')); await running;
    assert.equal(f.component.database.value, 'beta');
    assert.equal(f.component.infoMsg.value, '');
  } finally { f.dispose(); }
});

test('Server revision conflicts invalidate the loaded snapshot and never retry automatically', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    await f.component.refresh(); f.component.stageOperation('rebuild');
    const running = f.component.confirmOperation();
    f.latest('rebuild').reject({ response: { status: 409, data: { error: 'generation_revision_conflict', message: 'changed' } } });
    await running;
    assert.match(f.component.errorMsg.value, /generation_revision_conflict/);
    assert.equal(f.component.status.value, null);
    assert.equal(f.component.canManage.value, false);
    assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('Cancelled writes remain uncertain even if a transport delivers a late successful response', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    await f.component.refresh(); f.component.stageOperation('rebuild');
    const running = f.component.confirmOperation();
    f.component.cancelOperation();
    assert.equal(f.component.status.value, null);
    assert.equal(f.latest('rebuild').signal.aborted, true);
    f.latest('rebuild').resolve(successful('rebuild')); await running;
    assert.equal(f.component.canManage.value, false);
    assert.match(f.component.infoMsg.value, /刷新状态及审计/);
    assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('Database Admin permission errors are displayed and do not leave a usable management snapshot', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('db').resolve({ databases: ['alpha'] }); await settle();
    const running = f.component.refresh();
    f.latest('status').reject({ response: { status: 403, data: { error: 'forbidden', message: 'DatabaseAdmin required' } } });
    f.latest('profiles').reject({ response: { status: 403, data: { error: 'forbidden', message: 'DatabaseAdmin required' } } });
    f.latest('audit').reject({ response: { status: 403, data: { error: 'forbidden', message: 'DatabaseAdmin required' } } });
    await running;
    assert.match(f.component.errorMsg.value, /forbidden/);
    assert.equal(f.component.status.value, null);
    assert.equal(f.latest('profiles').signal.aborted, true);
    assert.equal(f.latest('audit').signal.aborted, true);
    assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});

test('Unavailable model configuration does not block status, audit, discard or retired cleanup', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  try {
    f.latest('db').resolve({ databases: ['alpha'] }); await settle();
    const running = f.component.refresh();
    f.latest('status').resolve(status('copilot-docs', { ...pending }));
    f.latest('profiles').reject({ response: { status: 503, data: { error: 'rag_unavailable', message: 'profile not configured' } } });
    f.latest('audit').resolve({ entries: [{ operationId: 'audit-row' }] });
    await running;
    assert.equal(f.component.status.value.activeRevision, 7);
    assert.equal(f.component.auditEntries.value.length, 1);
    assert.match(f.component.profileError.value, /rag_unavailable/);
    assert.equal(f.component.canRebuild.value, false);
    assert.equal(f.component.canResume.value, false);
    f.component.stageOperation('discard');
    assert.equal(f.component.pendingOperation.value.operation, 'discard');
    f.component.clearApproval();
    f.component.stageOperation('cleanup');
    assert.equal(f.component.pendingOperation.value.operation, 'cleanup');
    assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});

test('Unsafe revisions, unavailable profiles, no active snapshot and invalid cutoff cannot stage writes', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    await f.component.refresh();
    f.component.status.value.activeRevision = Number.MAX_SAFE_INTEGER + 1;
    f.component.stageOperation('rebuild'); assert.equal(f.component.pendingOperation.value, null);
    f.component.status.value.activeRevision = 0;
    f.component.stageOperation('rebuild'); assert.equal(f.component.pendingOperation.value, null);
    f.component.status.value.activeRevision = 7;
    f.component.selectedProfileId.value = 'arbitrary-url';
    f.component.stageOperation('rebuild'); assert.equal(f.component.pendingOperation.value, null);
    f.component.retiredBefore.value = Date.now() + 60_000;
    f.component.stageOperation('cleanup'); assert.equal(f.component.pendingOperation.value, null);
    assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});

test('Audit pagination replaces the bounded page and uses the server continuation token', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    await f.component.refresh();
    f.component.auditEntries.value = [{ operationId: 'old' }];
    await f.component.loadAudit(true);
    assert.equal(f.latest('audit').config.params.limit, 50);
    assert.equal(f.latest('audit').config.params.continuationToken, 'next-page');
    assert.deepEqual(f.component.auditEntries.value, []);
  } finally { f.dispose(); }
});

test('Unmount aborts owned reads and configured profile selection never accepts endpoints or secrets', { timeout: 5000 }, async () => {
  const f = await fixture({ autoReads: false });
  f.latest('db').resolve({ databases: ['alpha'] }); await settle();
  const running = f.component.refresh();
  const call = f.latest('status');
  f.dispose(); await running;
  assert.equal(call.signal.aborted, true);
  assert.equal(f.component.status.value, null);
  assert.match(descriptor.template.content, /:options="profileOptions"/);
  assert.doesNotMatch(descriptor.template.content, /v-model[^\n]*(?:apiKey|endpoint|path)/i);
});
