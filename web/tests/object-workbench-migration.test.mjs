import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import axios from 'axios';
import * as vue from 'vue';
import { compileScript, parse } from '@vue/compiler-sfc';

const { descriptor } = parse(readFileSync(new URL('../src/components/ObjectBucketWorkbench.vue', import.meta.url), 'utf8'), { filename: 'ObjectBucketWorkbench.vue' });
const compiled = compileScript(descriptor, { id: 'object-workbench-migration' });
const source = (name) => readFileSync(new URL(`../src/api/${name}.ts`, import.meta.url), 'utf8');
const synthetic = (exports) => new SyntheticModule(Object.keys(exports), function () {
  const entries = Object.entries(exports); const deadline = Date.now() + 1000;
  for (let index = 0; index < entries.length && index < 1000 && Date.now() < deadline; index += 1) this.setExport(...entries[index]);
});
const settle = async () => { const deadline = Date.now() + 1000; for (let index = 0; index < 8 && Date.now() < deadline; index += 1) { await Promise.resolve(); await vue.nextTick(); } };
const object = (key = 'North/Key:Original', bucket = 'Bucket:Original', versionId = 'V:Original') => ({ bucket, key, versionId, contentType: 'text/plain', sizeBytes: 8192,
  eTag: 'etag', sha256: 'sha', isDeleteMarker: false, createdUtc: '2026-10-06', updatedUtc: '2026-10-06', metadata: { secret: 'metadata-secret' }, tags: { secret: 'tag-secret' } });
const list = (objects = [object()], extras = {}) => ({ bucket: 'Bucket:Original', prefix: '', continuationToken: '', nextContinuationToken: null, maxKeys: 100,
  isTruncated: false, objects, ...extras });
const deny = (status = 403) => ({ response: { status, data: { message: 'server-secret', code: 'server-secret' } } });

async function fixture({ actualAxios = false, honorAbort = true } = {}) {
  const calls = []; const history = []; const notices = []; const disposers = []; const urls = []; const revoked = []; const downloads = []; const emitted = []; const paused = new Set();
  let bridge = null;
  const props = vue.reactive({ targetDb: 'alpha', bucket: 'Bucket:Original', buckets: [], loading: false, readOnly: false, permissionDenied: false });
  const connections = vue.reactive({ activeProfileId: 'first', activeBaseUrl: 'http://first.invalid', activeProfile: { name: 'First' } });
  const describe = (url, method, config = {}) => {
    const parsed = new URL(url, 'http://first.invalid'); const path = parsed.pathname.split('/').map(decodeURIComponent); const params = parsed.searchParams;
    const bucket = path[5] ?? ''; const key = path.slice(6).join('/');
    const action = url === '/v1/semantic-search/status' ? 'runtime' : url.includes('/images/') ? method === 'get' ? 'protected' : 'search'
      : path.length === 5 ? 'buckets' : params.has('list-type') ? 'objects' : params.has('tagging') ? 'tags' : params.has('legal-hold') ? 'hold'
        : params.has('versions') ? 'versions' : params.has('audit') ? 'audit' : params.has('uploads') ? 'multipart'
          : ['stats', 'lifecycle', 'retention', 'quota', 'policy', 'semantic', 'processing', 'thumbnail', 'presign'].find((name) => params.has(name))
            ?? (config.headers?.Range ? 'range' : key ? 'download' : 'bucket');
    return { action, bucket, key, params, parsed };
  };
  const defaultResponse = (call) => {
    const { action, bucket, key, params } = call; let data;
    const headers = { 'x-amz-version-id': params.get('versionId') || 'V:Original', 'content-type': 'text/plain', 'content-length': '8192' };
    let status = 200;
    if (action === 'buckets') data = [{ name: 'Bucket:Original', purpose: 'fixture', createdUtc: '2026-10-06', updatedUtc: '2026-10-06' }];
    else if (action === 'objects') data = list([object(`${params.get('prefix') || ''}North/Key:Original`, bucket)], { bucket, prefix: params.get('prefix') || '', continuationToken: params.get('continuation-token') || '' });
    else if (action === 'versions') data = { bucket, key: params.get('key'), versions: [object(params.get('key') || undefined, bucket)] };
    else if (action === 'tags') data = { tags: { secret: 'loaded-tag-secret' } };
    else if (action === 'hold') data = { bucket, key, versionId: params.get('versionId') || 'V:Original', enabled: true, reason: 'hold-secret' };
    else if (action === 'processing') data = { bucket, key, versionId: 'V:Original', jobId: 'job', status: 'completed', semanticImageId: 'image1', thumbnailUrl: 'fixture' };
    else if (action === 'thumbnail' || action === 'protected' || action === 'download') data = new Blob(['preview-secret']);
    else if (action === 'range') {
      const [start, requestedEnd] = call.config.headers.Range.slice(6).split('-').map(Number); const end = Math.min(requestedEnd, 8191);
      data = new Blob(['R'.repeat(Math.max(0, end - start + 1))]); status = 206; headers['content-range'] = `bytes ${start}-${end}/8192`; headers['content-length'] = String(data.size);
    } else if (action === 'audit') data = { bucket, entries: [{ id: 'audit', bucket, action: 'fixture', timestampUtc: '2026-10-06', details: { secret: 'audit-secret' } }] };
    else if (action === 'multipart') data = { bucket, uploads: [], isTruncated: false };
    else if (action === 'runtime') data = { enabled: true, ready: true, provider: 'fixture', profile: 'fixture', dimensions: 2, capabilities: [] };
    else if (action === 'search') data = { queryKind: 'text', profile: 'fixture', backend: 'fixture', hits: [{ id: 'image1', score: 1, distance: 0, contentType: 'image/png', contentUrl: '/v1/db/alpha/images/image1/content', sourceBucket: bucket, sourceKey: key, sizeBytes: 10 }] };
    else if (action === 'presign') data = { url: 'presign-secret', bucket, key, expiresUtc: '2026-10-06' };
    else data = { bucket, currentObjectCount: 1, policyJson: '{"secret":"policy-secret"}', thumbnailEnabled: true, thumbnailMaxWidth: 320, thumbnailMaxHeight: 320, thumbnailQuality: 80 };
    return { data, headers, status, statusText: 'OK', config: call.config };
  };
  const transport = (method, url, body, config = {}, native = false) => {
    const call = { ...describe(url, method, config), method, url, body, config, signal: config.signal }; calls.push(call);
    return new Promise((resolve, reject) => {
      const cleanup = () => config.signal?.removeEventListener('abort', abort);
      const abort = () => { if (honorAbort) { cleanup(); reject(Object.assign(new Error('cancelled'), { code: 'ERR_CANCELED' })); } };
      call.resolve = (data, headers, status) => { cleanup(); resolve(data === undefined ? defaultResponse(call) : { ...defaultResponse(call), data, ...(headers ? { headers } : {}), ...(status ? { status } : {}) }); };
      call.reject = (error) => { cleanup(); reject(error); };
      config.signal?.addEventListener('abort', abort, { once: true }); if (config.signal?.aborted && honorAbort) return abort();
      if (native || !paused.has(call.action) && (method === 'get' || call.action === 'search')) call.resolve();
    });
  };
  const clientModule = new SourceTextModule(stripTypeScriptTypes(source('client').replace('import.meta.env.BASE_URL', "'http://first.invalid'"), { mode: 'transform' }));
  await clientModule.link(() => synthetic({ default: { create: (config) => axios.create({ ...config, adapter: (request) => transport(request.method, request.url, request.data, request, true) }) } }));
  await clientModule.evaluate({ timeout: 3000 });
  const createApiClient = (getToken) => {
    if (actualAxios) return vue.markRaw(clientModule.namespace.createApiClient(getToken));
    return vue.markRaw({ defaults: { baseURL: 'http://first.invalid', headers: { common: {} } }, get: (url, config) => transport('get', url, undefined, config),
      post: (url, body, config) => transport('post', url, body, config), put: (url, body, config) => transport('put', url, body, config), delete: (url, config) => transport('delete', url, undefined, config) });
  };
  const auth = vue.reactive({ state: { token: 'first' }, api: createApiClient(() => auth.state.token) });
  const ui = ['NAlert', 'NButton', 'NDataTable', 'NEmpty', 'NInput', 'NInputNumber', 'NRadioButton', 'NRadioGroup', 'NSelect', 'NSpace', 'NSwitch', 'NTag', 'NText'];
  const modules = new Map([
    ['vue', synthetic({ ...vue, onBeforeUnmount: (callback) => disposers.push(callback) })],
    ['naive-ui', synthetic({ ...Object.fromEntries(ui.map((name) => [name, {}])), useMessage: () => Object.fromEntries(['success', 'error', 'warning'].map((kind) => [kind, (text) => notices.push({ kind, text })])) })],
    ['lucide-vue-next', synthetic(Object.fromEntries(['ChevronRight', 'Image', 'ImageUp', 'Layers3', 'RefreshCw', 'RotateCcw', 'Save', 'ScanSearch', 'Search'].map((name) => [name, {}])))],
    ['@/api/objectStorage', new SourceTextModule(stripTypeScriptTypes(source('objectStorage'), { mode: 'transform' }))],
    ['@/api/semanticSearch', new SourceTextModule(stripTypeScriptTypes(source('semanticSearch'), { mode: 'transform' }))],
    ['@/api/client', actualAxios ? clientModule : synthetic({ createApiClient })], ['@/api/studioNativeBridge', synthetic({ currentStudioNativeBridge: () => bridge })],
    ['@/stores/auth', synthetic({ useAuthStore: () => auth })], ['@/stores/connections', synthetic({ useConnectionsStore: () => connections })],
    ['@/stores/workbenchHistory', synthetic({ useWorkbenchHistoryStore: () => ({ record: (entry) => history.push(entry) }) })],
    ['@/utils/writeApproval', synthetic({ createWriteApprovalPlan: (value) => value })],
    ...['WorkbenchHistoryDrawer', 'WorkbenchResultPanel', 'WorkbenchSectionTabs', 'WriteApprovalPanel'].map((name) => [`@/components/${name}.vue`, synthetic({ default: {} })]),
  ]);
  const module = new SourceTextModule(stripTypeScriptTypes(compiled.content, { mode: 'transform' }));
  await module.link((name) => { assert.ok(modules.has(name), name); return modules.get(name); }); await module.evaluate({ timeout: 3000 });
  const previousDocument = globalThis.document; const previousCreate = URL.createObjectURL; const previousRevoke = URL.revokeObjectURL;
  globalThis.document = { body: { appendChild() {} }, createElement: () => ({ click() { downloads.push(this.download); }, remove() {} }) };
  URL.createObjectURL = () => { const value = `blob:fixture-${urls.length}`; urls.push(value); return value; }; URL.revokeObjectURL = (value) => revoked.push(value);
  const scope = vue.effectScope(); const component = scope.run(() => module.namespace.default.setup(props, { expose() {}, emit: (...args) => emitted.push(args) }));
  const dispose = () => { disposers.forEach((callback) => callback()); scope.stop(); globalThis.document = previousDocument; URL.createObjectURL = previousCreate; URL.revokeObjectURL = previousRevoke; };
  const latest = (action) => calls.filter((call) => call.action === action).at(-1);
  const stage = () => { component.uploadKey.value = 'Draft:Original'; component.uploadText.value = 'write-secret'; component.stageUploadText(); };
  await settle(); await settle();
  return { component, props, auth, connections, calls, history, notices, urls, revoked, downloads, emitted, latest, stage, dispose,
    pause: (...actions) => actions.forEach((action) => paused.add(action)), setBridge: (value) => { bridge = value; }, writes: () => calls.filter((call) => call.method !== 'get' && call.action !== 'search') };
}

test('list request is bounded before mapping and overreturned continuation is discarded', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.pause('objects'); f.component.listLimit.value = 5000; const run = f.component.loadObjects(true); const call = f.latest('objects'); assert.equal(call.params.get('max-keys'), '1000');
    const objects = Array.from({ length: 1001 }, (_, index) => object(`Key:${index}`)); Object.defineProperty(objects[1000], 'metadata', { get() { throw new Error('mapped discarded object'); } });
    call.resolve(list(objects, { isTruncated: true, nextContinuationToken: 'would-skip' })); await run;
    assert.equal(f.component.rows.value.length, 1000); assert.equal(f.component.cursor.value, null); assert.equal(f.component.hasMore.value, false);
    assert.equal(f.component.latestResult.value.end.truncated, true); assert.equal(f.history.at(-1).completeness, 'truncated');
    f.component.listLimit.value = 0; const small = f.component.loadObjects(true); assert.equal(f.latest('objects').params.get('max-keys'), '1'); f.latest('objects').resolve(list()); await small;
  } finally { f.dispose(); }
});

test('opaque continuation advances to cumulative 1000 and uses the retained preview count', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.pause('objects'); f.component.listLimit.value = 600; const first = f.component.loadObjects(true);
    f.latest('objects').resolve(list(Array.from({ length: 600 }, (_, index) => object(`Key:${index}`)), { isTruncated: true, nextContinuationToken: 'opaque-A' })); await first;
    const second = f.component.loadMore(); assert.equal(f.latest('objects').params.get('max-keys'), '400'); assert.equal(f.latest('objects').params.get('continuation-token'), 'opaque-A');
    f.latest('objects').resolve(list(Array.from({ length: 600 }, (_, index) => object(`Next:${index}`)), { continuationToken: 'opaque-A', isTruncated: true, nextContinuationToken: 'opaque-B' })); await second;
    assert.equal(f.component.rows.value.length, 1000); assert.equal(f.component.hasMore.value, false); assert.equal(f.history.at(-1).rowCount, 400);
  } finally { f.dispose(); }
});

test('wrong bucket, prefix, entry and non advancing continuation cannot replace current rows', { timeout: 10000 }, async () => {
  for (const extras of [{ bucket: 'wrong' }, { prefix: 'wrong' }, { objects: [object('Wrong', 'wrong')] }, { isTruncated: true, nextContinuationToken: '' }]) {
    const f = await fixture();
    try { f.pause('objects'); const run = f.component.loadObjects(true); f.latest('objects').resolve(list(undefined, extras)); await run; assert.equal(f.component.rows.value.length, 0); assert.equal(f.component.hasMore.value, false); assert.equal(f.component.objectState.value, 'error'); }
    finally { f.dispose(); }
  }
});

test('original database/Bucket/key and prefix whitespace survive encoding and list target validation', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.props.targetDb = 'North:DB'; f.props.bucket = ' Bucket:Original '; await settle(); f.pause('objects');
    f.component.currentPrefix.value = '/ Prefix:Original '; const run = f.component.loadObjects(true); const call = f.latest('objects');
    assert.match(call.url, /North%3ADB\/s3\/%20Bucket%3AOriginal%20/u); assert.equal(call.params.get('prefix'), ' Prefix:Original ');
    call.resolve(list([object(' Prefix:Original Key', f.props.bucket)], { bucket: f.props.bucket, prefix: ' Prefix:Original ' })); await run;
    assert.equal(f.history.at(-1).database, 'North:DB'); assert.equal(f.history.at(-1).target, ' Bucket:Original ');
  } finally { f.dispose(); }
});

test('all read paths latch 401/403 and clear objects, metadata, URLs, results, drafts and approvals', { timeout: 20000 }, async () => {
  const actions = ['buckets', 'objects', 'stats', 'runtime', 'versions', 'audit', 'multipart', 'tags', 'hold', 'processing', 'thumbnail', 'range', 'download', 'search', 'protected'];
  for (const action of actions) {
    const f = await fixture({ honorAbort: false });
    try {
      f.component.semanticText.value = 'prime'; await f.component.runSemanticSearch(); assert.ok(f.urls.length > 0);
      f.stage(); assert.ok(f.component.pendingOperations.value.length);
      f.pause(action); let run;
      if (action === 'buckets') run = f.component.loadBucketList(); else if (action === 'objects') run = f.component.loadObjects(true);
      else if (action === 'stats' || action === 'runtime') run = f.component.loadGovernance(f.props.bucket);
      else if (action === 'versions') run = f.component.loadVersions(); else if (action === 'audit') run = f.component.loadAudit();
      else if (action === 'multipart') run = f.component.loadMultipartSessions(true); else if (action === 'tags' || action === 'hold') run = f.component.loadSelection();
      else if (action === 'processing' || action === 'thumbnail') run = f.component.loadSelectedProcessing(); else if (action === 'range') run = f.component.loadPreview();
      else if (action === 'download') run = f.component.downloadSelectedObject(); else { f.component.semanticText.value = 'new query'; run = f.component.runSemanticSearch(); }
      await settle(); f.latest(action).reject(deny(action.length % 2 ? 401 : 403)); await run; await settle();
      assert.equal(f.component.objectState.value, 'permission', action); assert.equal(f.component.rows.value.length, 0); assert.equal(f.component.stats.value, null);
      assert.equal(f.component.previewText.value, ''); assert.equal(f.component.selectedThumbnailUrl.value, ''); assert.deepEqual(f.component.semanticHitUrls.value, {});
      assert.equal(f.component.latestResult.value, null); assert.equal(f.component.policyDraft.value, ''); assert.equal(f.component.uploadText.value, ''); assert.equal(f.component.pendingOperations.value.length, 0);
      const count = f.calls.length; await f.component.refreshAll(); await f.component.loadPreview(); await f.component.confirmPendingOperations(); assert.equal(f.calls.length, count);
      assert.ok(!JSON.stringify(f.history).includes('server-secret'));
    } finally { f.dispose(); }
  }
});

test('ordinary governance rejection followed by a later sibling denial still locks permission', { timeout: 10000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.pause('stats', 'lifecycle'); const run = f.component.loadGovernance(f.props.bucket); f.latest('stats').reject(new Error('ordinary-secret')); await settle();
    assert.equal(f.component.permissionDenied.value, false); f.latest('lifecycle').reject(deny()); await run;
    assert.equal(f.component.permissionDenied.value, true); assert.equal(f.component.stats.value, null);
  } finally { f.dispose(); }
});

test('permission latch survives same auth refresh and empty database/Bucket/profile/endpoint round trips', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.pause('audit'); const run = f.component.loadAudit(); f.latest('audit').reject(deny()); await run; const count = f.calls.length;
    f.auth.state = { token: 'first' }; f.props.targetDb = ''; f.props.bucket = ''; f.connections.activeProfileId = ''; f.connections.activeBaseUrl = '';
    f.props.targetDb = 'alpha'; f.props.bucket = 'Bucket:Original'; f.connections.activeProfileId = 'first'; f.connections.activeBaseUrl = 'http://first.invalid'; await settle();
    assert.equal(f.component.permissionDenied.value, true); assert.equal(f.calls.length, count);
  } finally { f.dispose(); }
});

test('cross database and authentication ABA suppress old list success, errors, finally and history', { timeout: 10000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.pause('objects'); const old = f.component.loadObjects(true); const call = f.latest('objects'); f.props.targetDb = 'beta'; await settle();
    const current = f.latest('objects'); call.resolve(list([object('old-secret')])); await old; assert.equal(f.component.loadingObjects.value, true);
    current.resolve(list([object('new-current')])); await settle(); assert.equal(f.component.rows.value[0].key, 'new-current');
    const stale = f.component.loadObjects(true); const error = f.latest('objects'); f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; error.reject(deny()); await stale;
    assert.equal(f.component.permissionDenied.value, false); assert.ok(!JSON.stringify(f.history).includes('old-secret'));
  } finally { f.dispose(); }
});

test('selected key and version ABA suppress tags, hold, versions, processing and thumbnail late data', { timeout: 10000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.pause('tags', 'hold', 'versions', 'processing'); const selection = f.component.loadSelection(); const tags = f.latest('tags'); const hold = f.latest('hold');
    const versions = f.component.loadVersions(); const oldVersions = f.latest('versions'); const processing = f.component.loadSelectedProcessing(); const oldProcessing = f.latest('processing');
    f.component.selectedKey.value = ''; f.component.selectedKey.value = 'North/Key:Original';
    f.component.rows.value[0].versionId = 'V:second'; f.component.rows.value[0].versionId = 'V:Original';
    tags.resolve({ tags: { secret: 'old-selection-secret' } }); hold.resolve({ bucket: f.props.bucket, key: 'North/Key:Original', versionId: 'V:Original', enabled: true, reason: 'old-secret' });
    oldVersions.resolve({ bucket: f.props.bucket, key: 'North/Key:Original', versions: [object('old-secret')] }); oldProcessing.resolve({ bucket: f.props.bucket, key: 'North/Key:Original', versionId: 'V:Original', thumbnailUrl: 'old-secret' });
    await Promise.all([selection, versions, processing]); assert.ok(!f.component.selectedTagsText.value.includes('old-selection')); assert.equal(f.component.processingStatus.value, null);
  } finally { f.dispose(); }
});

test('Range freezes version/mode and slices Blob before allocating or formatting more than 4096 bytes', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.pause('range'); f.component.rangeLength.value = 9000; f.component.previewMode.value = 'hex'; const run = f.component.loadPreview(); const call = f.latest('range');
    assert.equal(call.config.headers.Range, 'bytes=0-4095'); assert.equal(call.params.get('versionId'), 'V:Original');
    const blob = new Blob(['R'.repeat(8192)]); blob.arrayBuffer = () => { throw new Error('unbounded original Blob allocation'); };
    call.resolve(blob, { 'x-amz-version-id': 'V:Original', 'content-type': 'text/plain', 'content-range': 'bytes 0-4095/8192' }, 206); await run;
    assert.equal(f.component.previewText.value.split(' ').length, 4096); assert.match(f.component.rangeNotice.value, /截断/u);
    assert.equal(f.history.at(-1).completeness, 'truncated');
  } finally { f.dispose(); }
});

test('unsafe Range inputs and wrong response version or offset never display payload', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    const count = f.calls.length; f.component.rangeStart.value = Number.MAX_SAFE_INTEGER + 1; await f.component.loadPreview();
    f.component.rangeStart.value = Number.MAX_SAFE_INTEGER; f.component.rangeLength.value = 2; await f.component.loadPreview(); assert.equal(f.calls.length, count);
    f.component.rangeStart.value = 0; f.component.rangeLength.value = 10; f.pause('range');
    const run = f.component.loadPreview(); f.latest('range').resolve(new Blob(['secret']), { 'x-amz-version-id': 'wrong', 'content-range': 'bytes 0-5/100' }, 206); await run;
    assert.equal(f.component.previewText.value, '');
    const second = f.component.loadPreview(); f.latest('range').resolve(new Blob(['secret']), { 'x-amz-version-id': 'V:Original', 'content-range': 'bytes 5-10/100' }, 206); await second;
    assert.equal(f.component.previewText.value, '');
  } finally { f.dispose(); }
});

test('a short declared 206 range never formats extra bytes from an overreturned Blob', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.pause('range'); f.component.rangeLength.value = 100;
    const run = f.component.loadPreview();
    f.latest('range').resolve(new Blob(['kept-extra-secret']), { 'x-amz-version-id': 'V:Original', 'content-type': 'text/plain', 'content-range': 'bytes 0-3/4' }, 206);
    await run; assert.equal(f.component.previewText.value, 'kept'); assert.match(f.component.rangeNotice.value, /Range 0–3/u);
    assert.equal(f.history.at(-1).completeness, 'truncated');
  } finally { f.dispose(); }
});

test('new Range ownership and synchronous mode ABA keep stale formatted bytes out of preview', { timeout: 10000 }, async () => {
  const f = await fixture({ honorAbort: false });
  try {
    f.pause('range'); const old = f.component.loadPreview(); const first = f.latest('range'); const next = f.component.loadPreview(); const current = f.latest('range');
    current.resolve(); await next; first.resolve(new Blob(['old-secret'])); await old; assert.ok(!f.component.previewText.value.includes('old-secret'));
    const aba = f.component.loadPreview(); const call = f.latest('range'); f.component.previewMode.value = 'hex'; f.component.previewMode.value = 'text'; call.resolve(new Blob(['aba-secret'])); await aba;
    assert.equal(f.component.previewText.value, '');
  } finally { f.dispose(); }
});

test('unmount suppresses pending thumbnail, protected image and download URLs', { timeout: 10000 }, async () => {
  const f = await fixture({ honorAbort: false });
  f.pause('thumbnail', 'protected', 'download'); const processing = f.component.loadSelectedProcessing(); f.component.semanticText.value = 'query'; const search = f.component.runSemanticSearch(); const download = f.component.downloadSelectedObject();
  await settle(); const pending = ['thumbnail', 'protected', 'download'].map((action) => f.latest(action)); const count = f.urls.length;
  f.dispose(); pending.forEach((call) => call.resolve(new Blob(['late-secret']))); await Promise.all([processing, search, download]);
  assert.equal(f.urls.length, count); assert.equal(f.downloads.length, 0); assert.equal(f.component.selectedThumbnailUrl.value, '');
});

test('readonly gates every stage, file and confirm path while allowing reads, Range and download', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.stage(); f.props.readOnly = true; await settle(); f.component.uploadKey.value = 'Draft'; f.component.uploadText.value = 'write'; f.component.newBucketName.value = 'New';
    for (const name of ['stageCreateBucket', 'stageDeleteBucket', 'stageUploadFile', 'stageUploadText', 'stageSetTags', 'stageCopySelected', 'stageDeleteCurrent', 'stageDeleteSelected', 'stagePresign', 'stageSetLifecycle', 'stageApplyLifecycle', 'stageSetSemanticOptions', 'stageSemanticBackfill', 'stageRequeueSelectedObject', 'stageSetRetention', 'stageSetQuota', 'stageSetPolicy', 'stageSetLegalHold', 'stageInitiateMultipart', 'stageUploadPart', 'stageCompleteMultipart', 'stageAbortMultipart']) f.component[name]();
    await f.component.confirmPendingOperations(); const input = { files: [new Blob(['secret'])], value: 'chosen' }; f.component.onUploadFileChange({ target: input });
    assert.equal(input.value, ''); assert.equal(f.component.pendingOperations.value.length, 0); assert.equal(f.writes().length, 0);
    await f.component.loadPreview(); await f.component.downloadSelectedObject(); assert.equal(f.downloads.length, 1); assert.equal(f.component.objectState.value, 'readonly');
  } finally { f.dispose(); }
});

test('approval identity or draft changes invalidate the original plan before confirmation', { timeout: 10000 }, async () => {
  const f = await fixture();
  try { f.stage(); f.component.uploadText.value = 'changed'; await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 0);
    f.stage(); f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; await f.component.confirmPendingOperations(); assert.equal(f.writes().length, 0); }
  finally { f.dispose(); }
});

test('late presign response after database ABA cannot write URL, refresh or attribute success to new identity', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    f.component.stagePresign(); const run = f.component.confirmPendingOperations(); const call = f.latest('presign'); assert.ok(call);
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; call.resolve({ url: 'old-presign-secret', expiresUtc: '2026-10-06' }); await run;
    assert.equal(f.component.presignedUrl.value, ''); assert.equal(f.history.find((entry) => entry.title === 'Object operation batch').status, 'unknown');
  } finally { f.dispose(); }
});

test('native file picker response after context ABA does not fill the new upload draft', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    let resolve; f.setBridge({ openBinaryFile: () => new Promise((done) => { resolve = done; }) }); const run = f.component.pickUploadFile();
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha'; resolve({ canceled: false, fileName: 'old-secret.bin', content: new Blob(['secret']) }); await run;
    assert.equal(f.component.uploadFile.value, null);
  } finally { f.dispose(); }
});

test('both browser picker changes reject a stale dialog after database ABA and preserve direct legal file changes', { timeout: 10000 }, async () => {
  const f = await fixture();
  try {
    let clicked = 0; f.component.uploadFileInput.value = { click() { clicked += 1; } }; f.component.multipartFileInput.value = { click() { clicked += 1; } };
    await f.component.pickUploadFile(); await f.component.pickMultipartFile(); assert.equal(clicked, 2);
    f.props.targetDb = 'beta'; f.props.targetDb = 'alpha';
    const oldFile = new File(['old-secret'], 'old-dialog.bin', { type: 'text/plain' });
    const uploadInput = { files: [oldFile], value: 'old-upload' }; const multipartInput = { files: [oldFile], value: 'old-part' };
    f.component.onUploadFileChange({ target: uploadInput }); f.component.onMultipartFileChange({ target: multipartInput });
    assert.equal(uploadInput.value, ''); assert.equal(multipartInput.value, ''); assert.equal(f.component.uploadFile.value, null); assert.equal(f.component.multipartFile.value, null);
    const currentFile = new File(['current'], 'current.bin', { type: 'text/plain' });
    f.component.onUploadFileChange({ target: { files: [currentFile], value: 'current' } }); f.component.onMultipartFileChange({ target: { files: [currentFile], value: 'current' } });
    assert.equal(f.component.uploadFile.value.name, 'current.bin'); assert.equal(f.component.multipartFile.value.name, 'current.bin');
  } finally { f.dispose(); }
});

test('production Axios cancels Range before adapter dispatch on synchronous authentication ABA', { timeout: 10000 }, async () => {
  const f = await fixture({ actualAxios: true });
  try {
    f.component.rangeStart.value = 777; const run = f.component.loadPreview(); f.auth.state = { token: 'second' }; f.auth.state = { token: 'first' }; await run; await settle();
    assert.ok(!f.calls.some((call) => call.action === 'range' && call.config.headers.Range.startsWith('bytes=777-')));
  } finally { f.dispose(); }
});
