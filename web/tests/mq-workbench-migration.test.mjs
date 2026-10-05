import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const source = readFileSync(new URL('../src/components/SonnetMqWorkbench.vue', import.meta.url), 'utf8');
const { descriptor } = parse(source, { filename: 'SonnetMqWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'mq-workbench-migration' });
const apiSource = readFileSync(new URL('../src/api/mq.ts', import.meta.url), 'utf8');
const managementSource = readFileSync(new URL('../src/api/management.ts', import.meta.url), 'utf8');
const clientSource = readFileSync(new URL('../src/api/client.ts', import.meta.url), 'utf8');
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  const entries = Object.entries(exports);
  const deadline = Date.now() + 1000;
  for (let index = 0; index < entries.length && index < 1000 && Date.now() < deadline; index += 1) this.setExport(...entries[index]);
});
const settle = async () => { await Promise.resolve(); await Promise.resolve(); await vue.nextTick(); await Promise.resolve(); await Promise.resolve(); };
const denial = (status = 403) => ({ response: { status, data: { message: 'server-secret-body', code: 'server-secret-code' } } });
const item = (offset = 10, text = 'payload-visible', topic = 'Orders:Original') => ({ topic, offset,
  timestampUtc: '2026-10-06T00:00:00Z', payload: Buffer.from(text).toString('base64'), headers: { source: 'fixture' } });

function clockFixture() {
  const originalNow = Date.now;
  const originalSetTimeout = globalThis.setTimeout;
  const originalClearTimeout = globalThis.clearTimeout;
  let now = originalNow();
  let id = 0;
  const timers = new Map();
  Date.now = () => now;
  globalThis.setTimeout = (callback, delay = 0) => { const key = ++id; timers.set(key, { at: now + delay, callback }); return key; };
  globalThis.clearTimeout = (key) => timers.delete(key);
  return {
    async advance(milliseconds) {
      const target = now + milliseconds;
      const wallDeadline = originalNow() + 1000;
      for (let index = 0; index < 100 && originalNow() < wallDeadline; index += 1) {
        const due = [...timers.entries()].filter(([, timer]) => timer.at <= target).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
        if (!due) { now = target; await settle(); return; }
        now = due[1].at; timers.delete(due[0]); due[1].callback(); await settle(); await settle();
      }
      assert.fail('Fake clock exceeded 100 callbacks or one second wall time');
    },
    restore() { timers.clear(); Date.now = originalNow; globalThis.setTimeout = originalSetTimeout; globalThis.clearTimeout = originalClearTimeout; },
  };
}

async function fixture({ autoReads = true, honorAbort = true, actualAxios = false } = {}) {
  let autoReadResponses = autoReads;
  const calls = [];
  const history = [];
  const notices = [];
  const exports = [];
  const disposers = [];
  const props = vue.reactive({ targetDb: 'alpha', topic: 'Orders:Original', topics: [{ topic: 'Orders:Original', messageCount: 20, nextOffset: 100 }],
    loading: false, readOnly: false, permissionDenied: false });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  const defaults = (action, topic = props.topic) => action === 'topics' ? { topics: props.topics }
    : action === 'stats' ? { topic, nextOffset: 100, messageCount: 100, consumerOffsets: {} }
    : action === 'offsets' ? { topic, nextOffset: 100, consumers: [{ consumerGroup: ' Group:Original ', committedOffset: 0, lag: 100 }] }
    : action === 'retention' ? { topic, retainedStartOffset: 0, retainedEndOffset: 99, retainedMessages: 100 }
    : action === 'browse' ? { messages: [] } : action === 'publish' ? { topic, offset: 100 }
    : { topic, consumerGroup: ' Group:Original ', nextOffset: 101 };
  const clientModule = new SourceTextModule(stripTypeScriptTypes(clientSource.replace('import.meta.env.BASE_URL', "'http://first.invalid'"), { mode: 'transform' }));
  await clientModule.link((name) => {
    assert.equal(name, 'axios');
    return synthetic({ default: { create(config) {
      return axios.create({ ...config, adapter: async (request) => {
        const action = request.url.split('/').at(-1);
        calls.push({ action, url: request.url, body: typeof request.data === 'string' ? JSON.parse(request.data) : request.data,
          signal: request.signal, baseUrl: request.baseURL, token: request.headers.Authorization });
        return { config: request, data: defaults(action, decodeURIComponent(request.url.split('/').at(-2))), status: 200, statusText: 'OK', headers: {} };
      } });
    } } });
  });
  await clientModule.evaluate({ timeout: 3000 });
  const createApiClient = (getToken) => {
    if (actualAxios) return vue.markRaw(clientModule.namespace.createApiClient(getToken));
    const client = { defaults: { baseURL: 'http://first.invalid' }, post(url, body, config = {}) {
      const action = url.split('/').at(-1);
      const call = { action, url, body, signal: config.signal, baseUrl: client.defaults.baseURL, token: getToken() };
      calls.push(call);
      return new Promise((resolve, reject) => {
        const cleanup = () => config.signal?.removeEventListener('abort', abort);
        const abort = () => { if (honorAbort) { cleanup(); reject(Object.assign(new Error('cancelled'), { code: 'ERR_CANCELED' })); } };
        call.resolve = (data) => { cleanup(); resolve({ data }); };
        call.reject = (error) => { cleanup(); reject(error); };
        config.signal?.addEventListener('abort', abort, { once: true });
        if (config.signal?.aborted && honorAbort) return abort();
        if (autoReadResponses && !['browse', 'publish', 'ack'].includes(action)) call.resolve(defaults(action, decodeURIComponent(url.split('/').at(-2))));
      });
    } };
    return vue.markRaw(client);
  };
  const auth = vue.reactive({ state: { token: 'first' }, api: createApiClient(() => auth.state.token) });
  const uiNames = ['NAlert', 'NButton', 'NDataTable', 'NDatePicker', 'NDrawer', 'NDrawerContent', 'NEmpty', 'NInput', 'NInputNumber', 'NSelect', 'NSpace', 'NTab', 'NTabs', 'NTag', 'NText'];
  const modules = new Map([
    ['vue', synthetic({ ...vue, onMounted() {}, onBeforeUnmount: (callback) => disposers.push(() => {
      const previousWindow = globalThis.window;
      globalThis.window = { removeEventListener() {} };
      try { callback(); } finally {
        if (previousWindow === undefined) delete globalThis.window; else globalThis.window = previousWindow;
      }
    }) })],
    ['lucide-vue-next', synthetic(Object.fromEntries(['History', 'MessageSquareMore', 'PanelRightClose', 'PanelRightOpen', 'RefreshCw', 'Send', 'Upload'].map((name) => [name, {}])))],
    ['naive-ui', synthetic({ ...Object.fromEntries(uiNames.map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'warning', 'error', 'info'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['@/api/mq', new SourceTextModule(stripTypeScriptTypes(apiSource, { mode: 'transform' }))],
    ['@/api/management', new SourceTextModule(stripTypeScriptTypes(managementSource, { mode: 'transform' }))],
    ['@/api/client', actualAxios ? clientModule : synthetic({ createApiClient })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })],
    ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (options) => options })],
    ['@/utils/resultExport', synthetic({ downloadText: (fileName, value, contentType) => exports.push({ fileName, value, contentType }), safeFileStem: (value) => value })],
    ...['WorkbenchHistoryDrawer', 'WorkbenchResultPanel', 'WorkbenchSectionTabs', 'WriteApprovalPanel'].map((name) => [`@/components/${name}.vue`, synthetic({ default: {} })]),
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => { assert.ok(modules.has(name), `Unexpected dependency: ${name}`); return modules.get(name); });
  await module.evaluate({ timeout: 3000 });
  const scope = vue.effectScope();
  const component = scope.run(() => module.namespace.default.setup(props, { expose() {}, emit() {} }));
  const dispose = () => { disposers.forEach((callback) => callback()); scope.stop(); };
  const latest = (action) => calls.filter((call) => call.action === action).at(-1);
  const writes = () => calls.filter((call) => ['publish', 'ack'].includes(call.action));
  const stage = (topic = props.topic) => { component.publishTopic.value = topic; component.publishPayload.value = 'write-secret'; component.stagePublish(); };
  await settle(); await settle(); await settle();
  return { component, props, auth, connections, calls, history, notices, exports, latest, writes, stage, dispose,
    pauseReads: () => { autoReadResponses = false; } };
}

test('MQ Browse retains original names, bounds request and over-return before mapping, paging and export', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.browseLimit.value = 2;
    const run = f.component.browseFromInput();
    const browse = f.latest('browse');
    assert.match(browse.url, /alpha\/mq\/Orders%3AOriginal\/browse$/u);
    assert.equal(browse.body.maxCount, 2);
    browse.resolve({ messages: [item(10), item(40), item(90, 'discarded-secret')] });
    await run;
    assert.deepEqual(Array.from(f.component.rows.value, (row) => row.offset), [10, 40]);
    assert.equal(f.history.at(-1).rowCount, 2);
    assert.equal(f.history.at(-1).completeness, 'truncated');
    f.component.exportMessages();
    assert.ok(!f.exports[0].value.includes('discarded-secret'));
    const next = f.component.nextPage();
    assert.equal(f.latest('browse').body.fromOffset, 41);
    f.latest('browse').resolve({ messages: [] });
    await next;
  } finally { f.dispose(); }
});

test('Topics and each metadata route 401/403 latch, clear data and suppress a following Browse', { timeout: 5000 }, async () => {
  for (const action of ['topics', 'stats', 'offsets', 'retention']) {
    for (const status of [401, 403]) {
      const f = await fixture({ autoReads: false, honorAbort: false });
      try {
        f.stage();
        if (action !== 'topics') { f.latest('topics').resolve({ topics: f.props.topics }); await settle(); await settle(); }
        f.latest(action).reject(denial(status));
        await settle(); await settle();
        assert.equal(f.component.mqState.value, 'permission');
        assert.equal(f.component.localTopics.value.length, 0);
        assert.equal(f.component.rows.value.length, 0);
        assert.equal(f.component.stats.value, null);
        assert.equal(f.component.offsets.value, null);
        assert.equal(f.component.retention.value, null);
        assert.equal(f.component.publishPayload.value, '');
        assert.equal(f.component.pendingOperations.value.length, 0);
        const count = f.calls.length;
        await f.component.refreshAll();
        assert.equal(f.calls.length, count);
        assert.ok(!f.calls.some((call) => call.action === 'browse'));
        assert.ok(!f.component.errorMsg.value.includes('server-secret'));
      } finally { f.dispose(); }
    }
  }
});

test('Browse denial clears messages, results, headers, trends, approvals and drawer state', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.latest('browse').resolve({ messages: [item()] }); await settle();
    f.stage(); f.component.resultVisible.value = f.component.historyVisible.value = true;
    const run = f.component.browseFromInput(); f.latest('browse').reject(denial()); await run;
    assert.equal(f.component.mqState.value, 'permission');
    assert.equal(f.component.latestResult.value, null);
    assert.equal(f.component.selectedHeadersText.value, '{}');
    assert.equal(f.component.trendSamples.value.length, 0);
    assert.equal(f.component.previewPlan.value, null);
    assert.equal(f.component.resultVisible.value, false);
    assert.equal(f.component.historyVisible.value, false);
  } finally { f.dispose(); }
});

test('denial survives refreshed auth and empty database/profile/endpoint/topic plus metadata fallback', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.latest('browse').reject(denial()); await settle();
    const count = f.calls.length;
    f.auth.state = { token: 'first' }; f.props.topic = ''; f.props.targetDb = ''; f.connections.activeProfileId = ''; f.connections.activeBaseUrl = '';
    f.props.topics = [{ topic: 'fallback-is-not-selected' }]; await settle();
    f.props.targetDb = 'alpha'; f.props.topic = 'Orders:Original'; f.connections.activeProfileId = 'first'; f.connections.activeBaseUrl = 'http://first.invalid'; await settle();
    assert.equal(f.component.permissionDenied.value, true); assert.equal(f.calls.length, count);
    f.props.targetDb = 'beta'; await settle(); await settle();
    assert.equal(f.component.permissionDenied.value, false); assert.ok(f.calls.length > count);
  } finally { f.dispose(); }
});

test('new Browse and synchronous database ABA discard responses even when transport ignores Abort', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    const old = f.latest('browse');
    const current = f.component.loadMessages(40, true);
    f.latest('browse').resolve({ messages: [item(40, 'current-value')] }); await current;
    old.resolve({ messages: [item(10, 'old-secret')] }); await settle();
    assert.equal(f.component.rows.value[0].offset, 40);
    const aba = f.component.loadMessages(0, true); const stale = f.latest('browse');
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha';
    stale.resolve({ messages: [item(0, 'aba-secret')] }); await aba;
    assert.ok(!JSON.stringify(f.component.rows.value).includes('aba-secret'));
  } finally { f.dispose(); }
});

test('unmount cancels owned reads and suppresses their late payload', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  const call = f.latest('browse'); f.dispose();
  assert.equal(call.signal.aborted, true);
  call.resolve({ messages: [item(10, 'unmounted-secret')] }); await settle();
  assert.equal(f.component.rows.value.length, 0);
});

test('4096 original byte payload and header previews remain bounded while JSONL round-trips full loaded data', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    const original = item(10, 'L'.repeat(8192) + 'tail-secret');
    original.headers = Object.fromEntries(Array.from({ length: 40 }, (_, index) => [`Header:${index}`, 'H'.repeat(180)]));
    f.latest('browse').resolve({ messages: [original] }); await settle();
    for (const mode of ['text', 'json', 'hex', 'base64']) {
      f.component.payloadView.value = mode;
      assert.ok(!f.component.selectedPayloadText.value.includes('tail-secret'));
      assert.ok(f.component.selectedPayloadText.value.length <= (mode === 'hex' ? 12288 : mode === 'base64' ? 5464 : 4096));
    }
    assert.ok(f.component.selectedHeadersText.value.length <= 4096);
    assert.equal(f.component.mqState.value, 'longContent');
    f.component.exportMessages(); const exported = JSON.parse(f.exports[0].value);
    assert.equal(exported.payloadBase64, original.payload); assert.deepEqual(exported.headers, original.headers);
  } finally { f.dispose(); }
});

test('unsafe message/input offsets reject Ack, paging, Browse and Seek without rounding', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.latest('browse').resolve({ messages: [item(Number.MAX_SAFE_INTEGER + 1)] }); await settle();
    const count = f.calls.length;
    f.component.stageAckSelected(); await f.component.nextPage();
    f.component.fromOffset.value = Number.MAX_SAFE_INTEGER + 1; await f.component.browseFromInput();
    f.component.stageAck('group', 1.5);
    assert.equal(f.calls.length, count); assert.equal(f.component.pendingOperations.value.length, 0);
    f.component.stats.value = { topic: f.props.topic, nextOffset: Number.MAX_SAFE_INTEGER + 1, messageCount: 1 };
    f.component.stageAckHighWater(); assert.equal(f.component.pendingOperations.value.length, 0);
    f.component.seekTimeMs.value = Date.now(); await f.component.seekByTime(); assert.equal(f.calls.length, count);
  } finally { f.dispose(); }
});

test('readonly and prop permission gate direct publish, import, Ack and approval execution', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage(); f.props.readOnly = true;
    f.stage(); f.component.stageAck('group', 10); f.component.openPublisher(); await f.component.confirmPendingOperations();
    const input = { files: [{ text: async () => JSON.stringify({ topic: f.props.topic, payload: 'import-secret' }) }], value: 'selected' };
    await f.component.onMessageFileSelected({ target: input });
    assert.equal(f.writes().length, 0); assert.equal(f.component.pendingOperations.value.length, 0); assert.equal(f.component.publisherVisible.value, false);
    assert.equal(f.component.mqState.value, 'readonly'); f.props.permissionDenied = true; assert.equal(f.component.mqState.value, 'permission');
  } finally { f.dispose(); }
});

test('Publish freezes original database and item Topic including whitespace; mixed Topic approval shows actual targets', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage(' Topic:One '); f.stage('Topic:Two');
    assert.match(f.component.previewPlan.value.target, / Topic:One , Topic:Two/u);
    const run = f.component.confirmPendingOperations();
    assert.match(f.latest('publish').url, /mq\/%20Topic%3AOne%20\/publish$/u);
    f.latest('publish').resolve({ topic: ' Topic:One ', offset: 100 }); await settle();
    f.latest('publish').resolve({ topic: 'Topic:Two', offset: 110 }); await run;
    assert.equal(f.history.find((entry) => entry.action === 'operation').status, 'success'); assert.equal(f.writes().length, 2);
  } finally { f.dispose(); }
});

test('Ack retains original consumerGroup and accepts monotonic nextOffset beyond offset + 1', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.component.stageAck(' Group:Original ', 10); const run = f.component.confirmPendingOperations();
    assert.equal(f.latest('ack').body.consumerGroup, ' Group:Original ');
    f.latest('ack').resolve({ topic: f.props.topic, consumerGroup: ' Group:Original ', nextOffset: 50 }); await run;
    assert.equal(f.history.find((entry) => entry.action === 'operation').status, 'success');
  } finally { f.dispose(); }
});

test('missing, wrong-target, unsafe terminal and transport errors consume approvals once with unknown history', { timeout: 5000 }, async () => {
  for (const terminal of [null, { topic: 'wrong', offset: 1 }, { topic: 'Orders:Original', offset: Number.MAX_SAFE_INTEGER + 1 }, 'transport']) {
    const f = await fixture();
    try {
      f.stage(); const run = f.component.confirmPendingOperations();
      assert.equal(f.component.pendingOperations.value.length, 0);
      if (terminal === 'transport') f.latest('publish').reject(new Error('transport-secret')); else f.latest('publish').resolve(terminal);
      await run;
      assert.equal(f.history.at(-1).status, 'unknown'); assert.ok(!JSON.stringify(f.history).includes('transport-secret'));
      await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 1);
    } finally { f.dispose(); }
  }
});

test('write denial latches permission without retaining drafts or replaying approval', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    f.stage(); const run = f.component.confirmPendingOperations(); f.latest('publish').reject(denial()); await run;
    assert.equal(f.component.mqState.value, 'permission'); assert.equal(f.component.publishPayload.value, ''); assert.equal(f.component.latestResult.value, null);
    assert.equal(f.history.at(-1).status, 'error'); await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('late dispatched Publish after database ABA records original identity as unknown', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.stage(); const run = f.component.confirmPendingOperations(); const write = f.latest('publish');
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; write.resolve({ topic: 'Orders:Original', offset: 101 }); await run;
    const operation = f.history.find((entry) => entry.action === 'operation');
    assert.equal(operation.status, 'unknown'); assert.equal(operation.database, 'alpha'); assert.equal(f.component.latestResult.value, null);
    await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 1);
  } finally { f.dispose(); }
});

test('delayed JSONL file text cannot restage approvals after synchronous identity change', { timeout: 5000 }, async () => {
  const f = await fixture();
  try {
    let resolve; const text = new Promise((done) => { resolve = done; });
    const input = { files: [{ text: () => text }], value: 'selected' };
    const run = f.component.onMessageFileSelected({ target: input }); f.props.targetDb = 'beta'; f.props.targetDb = 'alpha';
    resolve(JSON.stringify({ topic: 'Old:Topic', payload: 'import-secret' })); await run;
    assert.equal(f.component.pendingOperations.value.length, 0); assert.equal(input.value, '');
  } finally { f.dispose(); }
});

test('production Axios client cancels MQ Browse before adapter dispatch on synchronous database ABA', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    const before = f.calls.filter((call) => call.action === 'browse').length;
    const run = f.component.loadMessages(80, true); f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; await run; await settle();
    assert.ok(!f.calls.some((call) => call.action === 'browse' && call.body.fromOffset === 80));
    assert.ok(f.calls.filter((call) => call.action === 'browse').length >= before);
  } finally { f.dispose(); }
});

test('production Axios interceptor cannot dispatch approved MQ Publish after synchronous authentication ABA', { timeout: 5000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    f.stage(); const run = f.component.confirmPendingOperations(); f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; await run;
    assert.equal(f.writes().length, 0); assert.equal(f.history.find((entry) => entry.action === 'operation').status, 'unknown');
  } finally { f.dispose(); }
});

test('auto stop and its 60-second deadline cannot abort an existing manual metadata sample', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false }); const clock = clockFixture();
  try {
    f.pauseReads(); const manual = f.component.refreshMonitorOnly(); const manualStats = f.latest('stats');
    const manualOffsets = f.latest('offsets'); const manualRetention = f.latest('retention');
    const count = f.calls.filter((call) => call.action === 'stats').length;
    f.component.autoRefresh.value = true; await settle(); await clock.advance(5000);
    assert.equal(f.calls.filter((call) => call.action === 'stats').length, count);
    f.component.autoRefresh.value = false; await settle(); assert.equal(manualStats.signal.aborted, false);
    f.component.autoRefresh.value = true; await settle(); await clock.advance(60_000);
    assert.equal(f.component.autoRefresh.value, false); assert.equal(manualStats.signal.aborted, false);
    manualStats.resolve({ topic: f.props.topic, nextOffset: 100, messageCount: 100 });
    manualOffsets.resolve({ topic: f.props.topic, nextOffset: 100, consumers: [] });
    manualRetention.resolve({ topic: f.props.topic, retainedStartOffset: 0, retainedEndOffset: 99, retainedMessages: 100 }); await manual;
  } finally { f.dispose(); clock.restore(); }
});

test('auto restart retains its new controller ownership when an old aborted sample returns late', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false }); const clock = clockFixture();
  try {
    f.pauseReads(); f.component.autoRefresh.value = true; await settle(); await clock.advance(5000);
    const oldStats = f.latest('stats'); const oldOffsets = f.latest('offsets'); const oldRetention = f.latest('retention');
    f.component.autoRefresh.value = false; await settle(); assert.equal(oldStats.signal.aborted, true);
    // Cancellation releases only the owned sample slot. The next generation
    // can start even when the old transport ignores Abort and returns later.
    f.component.autoRefresh.value = true; await settle(); await clock.advance(5000);
    const current = f.latest('stats'); assert.notEqual(current, oldStats); assert.equal(current.signal.aborted, false);
    oldStats.resolve({ topic: f.props.topic, nextOffset: 100, messageCount: 100 });
    oldOffsets.resolve({ topic: f.props.topic, nextOffset: 100, consumers: [] });
    oldRetention.resolve({ topic: f.props.topic, retainedStartOffset: 0, retainedEndOffset: 99, retainedMessages: 100 }); await settle(); await settle();
    f.component.autoRefresh.value = false; await settle(); assert.equal(current.signal.aborted, true);
  } finally { f.dispose(); clock.restore(); }
});

test('auto sampling dispatches no more than 12 single-flight rounds and stops at its 60-second budget', { timeout: 5000 }, async () => {
  const f = await fixture(); const clock = clockFixture();
  try {
    const count = f.calls.filter((call) => call.action === 'stats').length;
    f.component.autoRefresh.value = true; await settle(); await clock.advance(60_000);
    const rounds = f.calls.filter((call) => call.action === 'stats').length - count;
    assert.ok(rounds > 0 && rounds <= 12); assert.equal(f.component.autoRefresh.value, false);
    const stopped = f.calls.length; await clock.advance(60_000); assert.equal(f.calls.length, stopped);
    f.component.autoRefresh.value = true; await settle(); await clock.advance(5000);
    assert.ok(f.calls.length > stopped);
  } finally { f.dispose(); clock.restore(); }
});

test('Seek final Browse shares the original 60-second deadline and late ABA cannot change selection', { timeout: 5000 }, async () => {
  for (const boundary of ['deadline', 'aba']) {
    const f = await fixture({ honorAbort: false }); const clock = clockFixture();
    try {
      f.component.seekTimeMs.value = Date.parse('2026-10-05T23:59:59Z');
      const run = f.component.seekByTime(); const window = f.latest('browse');
      window.resolve({ messages: [item(20)] }); await settle(); await settle();
      const final = f.latest('browse'); assert.notEqual(final, window); assert.equal(final.body.fromOffset, 20);
      if (boundary === 'deadline') await clock.advance(60_000);
      else { f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; }
      assert.equal(final.signal.aborted, true);
      final.resolve({ messages: [item(20, 'old-seek-secret')] }); await run;
      assert.equal(f.component.selectedOffset.value, null);
      assert.ok(!JSON.stringify(f.component.rows.value).includes('old-seek-secret'));
    } finally { f.dispose(); clock.restore(); }
  }
});

test('metadata fallback Topic changes clear prior payloads and synchronous fallback ABA invalidates reads and approvals', { timeout: 5000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.props.topic = ''; await settle(); await settle(); await settle();
    const topicA = 'Orders:Original';
    const initial = f.latest('browse'); initial.resolve({ messages: [item(10, 'fallback-a-secret', topicA)] }); await settle();
    assert.equal(f.component.activeTopic.value, topicA); assert.equal(f.component.rows.value.length, 1);
    f.stage('Publish:Draft');
    const staleRun = f.component.loadMessages(0, true); const stale = f.latest('browse');
    f.props.topics = [{ topic: 'Fallback:B', nextOffset: 100, messageCount: 100 }];
    assert.equal(f.component.activeTopic.value, 'Fallback:B'); assert.equal(f.component.rows.value.length, 0);
    assert.equal(f.component.stats.value, null); assert.equal(f.component.offsets.value, null); assert.equal(f.component.retention.value, null);
    assert.equal(f.component.pendingOperations.value.length, 0); assert.equal(f.component.publishPayload.value, '');
    f.props.topics = [{ topic: topicA, nextOffset: 100, messageCount: 100 }];
    assert.equal(f.component.activeTopic.value, topicA); assert.equal(stale.signal.aborted, true);
    stale.resolve({ messages: [item(10, 'fallback-aba-secret', topicA)] }); await staleRun;
    assert.ok(!JSON.stringify(f.component.rows.value).includes('fallback-aba-secret'));
    await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 0);
  } finally { f.dispose(); }
});
