import { expect, test, type Page, type Route } from '@playwright/test';

// This is a real Vue component in a browser with a fixture API adapter. It
// proves client approval/terminal handling only; it is not Server or host
// permission, three-host, installation, or release evidence.
const database = 'FactoryDB:East';
const bucket = 'InspectionMedia:Original';
const key = 'Write:Original.txt';
const now = '2026-10-06T00:00:00.000Z';
const bucketInfo = { name: bucket, purpose: 'terminal fixture', createdUtc: now, updatedUtc: now, objectCount: 1, totalBytes: 5 };

type WriteMode = 'success' | 'disconnect' | 'wrong-target' | 'missing-terminal';
interface Evidence { writes: Array<{ method: string; path: string; body: string }>; unexpected: string[] }
interface Harness {
  stageText: () => Promise<void>;
  seedBatch: (count: number) => Promise<void>;
  confirm: () => Promise<void>;
  snapshot: () => { pending: number; resultRows: number; resultError: string; error: string };
  unmount: () => void;
}
type FixtureWindow = Window & { wb25ObjectHarness: Harness };
test.setTimeout(30_000);

test('approval is consumed once, a valid terminal is success, and repeated confirm does not dispatch again', async ({ page }) => {
  const gate = deferred();
  const evidence = await prepare(page, () => 'success', () => gate.wait);
  try {
    await openObject(page); await mountHarness(page); await ready(page);
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.stageText());
    await expect(approval(page)).toContainText('Put text object');
    await approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
    await expect.poll(() => evidence.writes.length, { timeout: 5_000 }).toBe(1);
    // The first PUT is still held. A concurrent confirmation cannot dispatch.
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
    expect(evidence.writes).toHaveLength(1);
    expect(await latestOperation(page)).toBeUndefined();
    gate.release();
    await expect.poll(async () => (await latestOperation(page))?.status, { timeout: 5_000 }).toBe('success');
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
    expect(evidence.writes).toHaveLength(1);
    const state = await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.snapshot());
    expect(state.pending).toBe(0);
    expect(state.resultError).toBe('');
    expect(evidence.writes[0]).toMatchObject({ method: 'PUT', path: `/v1/db/${database}/s3/${bucket}/${key}` });
    cleanEvidence(evidence);
  } finally { gate.release(); }
});

for (const kind of ['wrong-target', 'missing-terminal'] as const) {
  test(`${kind} terminal is unknown, retained in history, and never replayed`, async ({ page }) => {
    const evidence = await prepare(page, () => kind);
    await openObject(page); await mountHarness(page); await ready(page);
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.stageText());
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
    expect(evidence.writes).toHaveLength(1);
    const terminal = await latestOperation(page);
    expect(terminal?.status).toBe('unknown');
    expect(terminal?.completeness).toBe('unknown');
    expect(terminal?.database).toBe(database); expect(terminal?.target).toBe(bucket);
    expect(JSON.stringify(terminal)).not.toContain('server-secret');
    const message = (await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.snapshot())).resultError;
    expect(message).toMatch(/未知/u); expect(message).toMatch(/核对/u); expect(message).toMatch(/不.*重放/u);
    await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
    expect(evidence.writes).toHaveLength(1);
    cleanEvidence(evidence);
  });
}

test('transport disconnect is unknown, preserves the original history context, and is not retried by confirm', async ({ page }) => {
  const evidence = await prepare(page, () => 'disconnect');
  await openObject(page); await mountHarness(page); await ready(page);
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.stageText());
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
  await expect.poll(() => evidence.writes.length, { timeout: 5_000 }).toBe(1);
  const terminal = await latestOperation(page);
  expect(terminal?.status).toBe('unknown'); expect(terminal?.database).toBe(database); expect(terminal?.target).toBe(bucket);
  expect(terminal?.completeness).toBe('unknown');
  const message = (await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.snapshot())).resultError;
  expect(message).toMatch(/未知/u); expect(message).toMatch(/核对/u); expect(message).toMatch(/不.*重放/u);
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
  expect(evidence.writes).toHaveLength(1);
  cleanEvidence(evidence);
});

test('synthetic batch limit accepts 1000 operations and rejects 1001 without HTTP dispatch or replay', async ({ page }) => {
  const evidence = await prepare(page, () => 'success');
  await openObject(page); await mountHarness(page); await ready(page);
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.seedBatch(1000));
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
  await expect.poll(async () => (await latestOperation(page))?.status, { timeout: 5_000 }).toBe('success');
  const accepted = await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.snapshot());
  expect(accepted.resultRows).toBe(1); expect(evidence.writes).toHaveLength(0);
  expect((await latestOperation(page))?.rowCount).toBe(1000);
  const historyCount = (await history(page)).filter((entry) => entry.title === 'Object operation batch').length;
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.seedBatch(1001));
  await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.confirm());
  expect(evidence.writes).toHaveLength(0);
  expect((await page.evaluate(() => (window as FixtureWindow).wb25ObjectHarness.snapshot())).pending).toBe(0);
  expect((await history(page)).filter((entry) => entry.title === 'Object operation batch')).toHaveLength(historyCount);
  cleanEvidence(evidence);
});

function surface(page: Page) { return page.getByTestId('workbench-bucket'); }
function approval(page: Page) { return page.locator('.write-approval'); }
async function openObject(page: Page): Promise<void> {
  await page.goto(`/admin/app/sql?tool=bucket&database=${encodeURIComponent(database)}&model=bucket&node=${encodeURIComponent(bucket)}`);
  await expect(surface(page)).toHaveAttribute('data-database', database);
}
async function ready(page: Page): Promise<void> {
  await expect.poll(async () => surface(page).locator('.object-grid .object-key-button').count(), { timeout: 5_000 }).toBe(1);
}
async function history(page: Page): Promise<Array<Record<string, any>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, any>> }).entries);
}
async function latestOperation(page: Page): Promise<Record<string, any> | undefined> {
  return (await history(page)).find((entry) => entry.title === 'Object operation batch');
}
function cleanEvidence(evidence: Evidence): void { expect(evidence.unexpected).toEqual([]); }

async function mountHarness(page: Page): Promise<void> {
  const componentPath = '/src/components/ObjectBucketWorkbench.vue'; const authPath = '/src/stores/auth.ts';
  const [source, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(content: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(content);
    if (!match) throw new Error(`Object terminal harness needs Vite ${name}.`);
    return match[1];
  }
  const modules = { vue: dependency(source, 'vue'), naive: dependency(source, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, bucket, bucketInfo, key }) => {
    const [vue, naive, piniaModule, component, authModule] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div'); host.id = 'wb25-object-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, bucket, buckets: [bucketInfo], readOnly: false, permissionDenied: false });
    const componentRef = vue.ref(null); const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, { ...props, ref: componentRef }) }) });
    app.use(pinia); authModule.useAuthStore(pinia).setApiBaseUrl('/'); app.mount(host);
    const setup = (): Record<string, any> => componentRef.value.$.setupState;
    (window as FixtureWindow).wb25ObjectHarness = {
      stageText: async () => { const state = setup(); state.uploadKey = key; state.uploadText = 'fixture-write'; state.uploadContentType = 'text/plain'; state.stageUploadText(); await vue.nextTick(); },
      seedBatch: async (count) => { const operations = Array.from({ length: count }, (_, index) => ({
        id: `batch_${index}`, expectedTarget: `Batch:${index}`, label: 'Batch fixture operation', detail: `Batch:${index}`, severity: 'write', command: `PUT /fixture/${index}`,
        run: async () => ({ action: 'batch.fixture', target: `Batch:${index}`, succeeded: true, affected: 1, detail: 'accepted' }),
      })); setup().pendingOperations = operations; await vue.nextTick(); },
      confirm: async () => { await setup().confirmPendingOperations(); await vue.nextTick(); },
      snapshot: () => { const state = setup(); return { pending: state.pendingOperations?.length ?? 0, resultRows: state.latestResult?.rows?.length ?? 0, resultError: state.latestResult?.error?.message ?? '', error: state.errorMsg }; },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database, bucket, bucketInfo, key });
}

async function prepare(page: Page, mode: () => WriteMode, holdWrite?: () => Promise<void>): Promise<Evidence> {
  const evidence: Evidence = { writes: [], unexpected: [] };
  const origin = new URL(test.info().project.use.baseURL as string).origin;
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    class QuietEventSource { static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2; readonly readyState = 1; url = ''; withCredentials = false; onopen = null; onmessage = null; onerror = null; addEventListener() {} removeEventListener() {} dispatchEvent() { return true; } close() {} }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database });
  await page.route('**/*', async (route) => {
    const request = route.request(); const url = new URL(request.url()); const method = request.method();
    if (url.origin !== origin) { evidence.unexpected.push(`external ${method} ${url.origin}${url.pathname}`); return route.abort('blockedbyclient'); }
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb25-fixture', userCount: 1, databaseCount: 1 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 1, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [database] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, provider: 'fixture', profile: 'fixture', dimensions: 0, configuredBackend: 'fixture', effectiveBackend: 'fixture', capabilities: [] });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && matched[1] === database) {
      const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces' || suffix === '/vector/indexes' || suffix === '/fulltext/indexes' || suffix === '/mq/topics' || suffix === '/graphs') return json(route, []);
      if (suffix === '/s3' && method === 'GET') return json(route, [bucketInfo]);
      if (suffix.startsWith(`/s3/${bucket}`)) {
        if (method === 'PUT' && suffix === `/s3/${bucket}/${key}` && !url.search) {
          evidence.writes.push({ method, path, body: request.postData() ?? '' });
          if (holdWrite) await holdWrite();
          const responseMode = mode();
          if (responseMode === 'disconnect') return route.abort('connectionfailed');
          if (responseMode === 'wrong-target') return json(route, { ...object(), bucket: 'Wrong:Bucket', key: 'Wrong:Key' });
          if (responseMode === 'missing-terminal') return json(route, {});
          return json(route, object());
        }
        if (method !== 'GET') { evidence.unexpected.push(`${method} ${path}${url.search}`); return json(route, { code: 'wb25_fixture_write_forbidden' }, 501); }
        const query = url.searchParams;
        if (query.has('list-type')) return json(route, { bucket, prefix: '', maxKeys: 100, continuationToken: '', nextContinuationToken: null, isTruncated: false, objects: [object()] });
        if (query.has('stats')) return json(route, { bucket, currentObjectCount: 1, currentSizeBytes: 5, objectVersionCount: 1, objectVersionSizeBytes: 5, deleteMarkerCount: 0, multipartUploadCount: 0, multipartPartCount: 0, multipartPartSizeBytes: 0 });
        if (query.has('lifecycle') || query.has('retention') || query.has('quota')) return json(route, { bucket, updatedUtc: now });
        if (query.has('policy')) return json(route, { bucket, policyJson: null, updatedUtc: now });
        if (query.has('semantic')) return json(route, { bucket, asyncIngestionEnabled: false, thumbnailEnabled: false, thumbnailMaxWidth: 320, thumbnailMaxHeight: 320, thumbnailQuality: 80, updatedUtc: now });
        if (query.has('versions')) return json(route, { bucket, key: query.get('key'), versions: [object()] });
        if (query.has('tagging')) return json(route, { tags: {} });
        if (query.has('legal-hold')) return json(route, { bucket, key, versionId: 'Version:Original', enabled: false, reason: null, updatedUtc: now });
        if (query.has('processing')) return json(route, { bucket, key, versionId: 'Version:Original', jobId: 'WB25:Job', operation: 'none', status: 'pending', semanticRequested: false, thumbnailRequested: false, attempts: 0, createdUtc: now, updatedUtc: now });
        if (query.has('audit')) return json(route, { bucket, entries: [] });
        if (query.has('uploads')) return json(route, { bucket, maxUploads: 100, continuationToken: '', nextContinuationToken: null, isTruncated: false, uploads: [] });
      }
    }
    evidence.unexpected.push(`${method} ${path}${url.search}`); return json(route, { code: 'wb25_contract_not_mocked' }, 501);
  });
  return evidence;
}
function object() { return { bucket, key, versionId: 'Version:Original', contentType: 'text/plain', sizeBytes: 5, eTag: 'etag', sha256: 'sha', isDeleteMarker: false, createdUtc: now, updatedUtc: now, metadata: {}, tags: {} }; }
function deferred() {
  let resolve!: () => void; let released = false;
  const wait = new Promise<void>((done) => { resolve = done; });
  const timer = setTimeout(() => release(), 10_000);
  const release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } };
  return { wait, release };
}
async function json(route: Route, body: unknown, status = 200): Promise<void> { await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }); }
