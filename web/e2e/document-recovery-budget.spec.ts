import { expect, test, type Page, type Route } from '@playwright/test';

// Real routed Vue components and a Vite-only prop harness with API fixtures.
// This does not establish Server authorization or any desktop host evidence.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const collectionName = 'DeviceProfiles:Main';
const createdUtc = '2026-10-06T00:00:00Z';
const collection = {
  name: collectionName,
  jsonIndexes: [{ name: 'HiddenIndexPayload', paths: ['$.site'], isUnique: false, isSparse: false, createdUtc, includedInBackup: true, rebuildable: true }],
  fullTextIndexes: [],
  validator: { rules: [{ path: '$.HiddenValidatorPayload', required: true, type: 'string' }], validationAction: 'error' },
};

interface ReadRequest {
  database: string;
  action: 'find' | 'count' | 'aggregate' | 'distinct';
  body: Record<string, unknown>;
}

interface Evidence {
  reads: ReadRequest[];
  writes: string[];
  unexpected: string[];
}

interface FixtureOptions {
  read?: (route: Route, request: ReadRequest) => Promise<void>;
  more?: boolean;
}

interface Harness {
  setDatabase: (database: string) => Promise<void>;
  setPermission: (denied: boolean) => Promise<void>;
  refreshSchema: () => Promise<void>;
  unmount: () => void;
}

type HarnessWindow = Window & { wb18DocumentHarness: Harness };

test.setTimeout(30_000);

test('explicit permission recovery keeps payload hidden, remains locked on failure, and starts a fresh Find', async ({ page }) => {
  let mode: 'normal' | 'deny' | 'fail' | 'recover' = 'normal';
  const gate = boundedGate();
  let recoveryPending = 0;
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action !== 'find') return defaultRead(route, request);
    if (mode === 'deny') return denied(route);
    if (mode === 'fail') return json(route, { message: 'Fixture recovery unavailable' }, 503);
    if (mode === 'recover') {
      recoveryPending += 1;
      await gate.wait;
      return findResult(route, 'Fresh recovered payload');
    }
    return findResult(route, 'Old payload must disappear', true);
  } });
  try {
    await openDocument(page);
    await assertState(page, 'longContent');
    await queryTab(page);
    await page.getByPlaceholder('IDs, one per line').fill('old-document-id');
    await page.getByPlaceholder('Continuation token', { exact: true }).fill('old-cursor');
    await page.getByPlaceholder('Projection JSON array or lines: name=$.path').fill('site=$.site');
    await page.getByPlaceholder('Sort JSON array or lines: $.score desc').fill('$.score desc');
    mode = 'deny';
    await surface(page).getByRole('button', { name: 'Run find', exact: true }).click();
    await assertPermissionPayloadHidden(page);
    const lockedReads = evidence.reads.length;
    await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click();
    await assertPermissionPayloadHidden(page);
    expect(evidence.reads.length).toBe(lockedReads);
    mode = 'fail';
    await recoverButton(page).click();
    await assertPermissionPayloadHidden(page);
    await expect(recoverButton(page)).toBeEnabled();
    mode = 'recover';
    await recoverButton(page).click();
    await expect.poll(() => recoveryPending, { timeout: 5_000 }).toBe(1);
    await assertPermissionPayloadHidden(page);
    await expect(recoverButton(page)).toBeDisabled();
    expect(evidence.reads.at(-1)).toMatchObject({ database: east, action: 'find', body: { limit: 100, skip: 0 } });
    for (const key of ['id', 'ids', 'filter', 'projection', 'sort', 'continuationToken']) {
      expect(evidence.reads.at(-1)?.body[key]).toBeUndefined();
    }
    gate.release();
    await assertState(page, 'normal');
    await expect(surface(page)).toContainText('Fresh recovered payload');
    await expect(surface(page)).not.toContainText('Old payload must disappear');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await queryTab(page);
    await expect(page.getByPlaceholder('IDs, one per line')).toHaveValue('');
    await expect(page.getByPlaceholder('Continuation token', { exact: true })).toHaveValue('');
    await expect(page.getByPlaceholder('Projection JSON array or lines: name=$.path')).toHaveValue('');
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('a read denial clears a pending write approval and recovery never restores its draft', async ({ page }) => {
  let denyNext = false;
  const gate = boundedGate();
  let pending = 0;
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action === 'find' && denyNext) {
      denyNext = false;
      pending += 1;
      await gate.wait;
      return denied(route);
    }
    return defaultRead(route, request);
  } });
  try {
    await openDocument(page);
    await assertState(page, 'normal');
    denyNext = true;
    await surface(page).getByRole('button', { name: 'Browse', exact: true }).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await surface(page).locator('.document-tabs .n-tabs-tab[data-name="edit"]').click();
    await page.getByPlaceholder('Document ID', { exact: true }).fill('OldWriteDraftID');
    await page.locator('.document-inspector-section textarea').fill('{ "OldWriteDraftPayload": true }');
    await surface(page).getByRole('button', { name: 'Stage insert', exact: true }).click();
    await expect(page.getByRole('dialog')).toContainText('OldWriteDraftID');
    expect(evidence.writes).toEqual([]);
    gate.release();
    await assertPermissionPayloadHidden(page);
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await recoverButton(page).click();
    await assertState(page, 'normal');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await surface(page).locator('.document-tabs .n-tabs-tab[data-name="edit"]').click();
    await expect(page.getByPlaceholder('Document ID', { exact: true })).not.toHaveValue('OldWriteDraftID');
    await expect(page.locator('.document-inspector-section textarea')).not.toHaveValue(/OldWriteDraftPayload/u);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('external permissionDenied cannot be overridden by the local recovery control', async ({ page }) => {
  const evidence = await prepare(page);
  await openDocument(page);
  await mountComponentHarness(page, { permissionDenied: true });
  await assertPermissionPayloadHidden(page);
  const reads = evidence.reads.length;
  await expect(recoverButton(page)).toBeDisabled();
  await harness(page, 'refreshSchema');
  await assertPermissionPayloadHidden(page);
  expect(evidence.reads.length).toBe(reads);
  assertFixtureEvidence(evidence);
});

test('external denial arriving during recovery rejects its success and preserves the local lock', async ({ page }) => {
  let mode: 'normal' | 'deny' | 'recover' = 'normal';
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action !== 'find') return defaultRead(route, request);
    if (mode === 'deny') return denied(route);
    if (mode === 'recover') {
      pending += 1;
      await gate.wait;
      try { await findResult(route, 'Denied recovery must not display'); }
      finally { completed += 1; }
      return;
    }
    return defaultRead(route, request);
  } });
  try {
    await openDocument(page);
    await mountComponentHarness(page);
    await assertState(page, 'normal');
    mode = 'deny';
    await surface(page).getByRole('button', { name: 'Browse', exact: true }).click();
    await assertPermissionPayloadHidden(page);
    mode = 'recover';
    await recoverButton(page).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await harness(page, 'setPermission', true);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await assertPermissionPayloadHidden(page);
    await harness(page, 'setPermission', false);
    await assertPermissionPayloadHidden(page);
    await expect(recoverButton(page)).toBeEnabled();
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('database ABA does not let an earlier recovery response unlock a new denial', async ({ page }) => {
  let mode: 'normal' | 'deny' | 'recover' = 'normal';
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action !== 'find' || request.database === west) return defaultRead(route, request);
    if (mode === 'deny') return denied(route);
    if (mode === 'recover') {
      pending += 1;
      await gate.wait;
      try { await findResult(route, 'Stale ABA recovery'); }
      finally { completed += 1; }
      return;
    }
    return defaultRead(route, request);
  } });
  try {
    await openDocument(page);
    await mountComponentHarness(page);
    await assertState(page, 'normal');
    mode = 'deny';
    await surface(page).getByRole('button', { name: 'Browse', exact: true }).click();
    await assertPermissionPayloadHidden(page);
    mode = 'recover';
    await recoverButton(page).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await harness(page, 'setDatabase', west);
    await expect(surface(page)).toHaveAttribute('data-database', west);
    mode = 'deny';
    await harness(page, 'setDatabase', east);
    await assertPermissionPayloadHidden(page);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await assertPermissionPayloadHidden(page);
    await expect(recoverButton(page)).toBeEnabled();
    expect(evidence.reads.some((request) => request.database === west)).toBe(true);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('unmounted recovery does not expose a late payload or append success history', async ({ page }) => {
  let mode: 'normal' | 'deny' | 'recover' = 'normal';
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action !== 'find') return defaultRead(route, request);
    if (mode === 'deny') return denied(route);
    if (mode === 'recover') {
      pending += 1;
      await gate.wait;
      try { await findResult(route, 'Unmounted late recovery'); }
      finally { completed += 1; }
      return;
    }
    return defaultRead(route, request);
  } });
  try {
    await openDocument(page);
    await mountComponentHarness(page);
    await assertState(page, 'normal');
    mode = 'deny';
    await surface(page).getByRole('button', { name: 'Browse', exact: true }).click();
    await assertPermissionPayloadHidden(page);
    mode = 'recover';
    await recoverButton(page).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    const before = await historyEntries(page);
    await harness(page, 'unmount');
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(surface(page)).toHaveCount(0);
    await expect(page.locator('body')).not.toContainText('Unmounted late recovery');
    expect(await historyEntries(page)).toEqual(before);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('readonly recovery restores reads and export while write entries remain disabled', async ({ page }) => {
  let denyFind = false;
  const evidence = await prepare(page, { read: (route, request) => request.action === 'find' && denyFind ? denied(route) : defaultRead(route, request) });
  await openDocument(page);
  await mountComponentHarness(page, { readOnly: true });
  await assertState(page, 'readonly');
  denyFind = true;
  await surface(page).getByRole('button', { name: 'Browse', exact: true }).click();
  await assertPermissionPayloadHidden(page);
  denyFind = false;
  await recoverButton(page).click();
  await assertState(page, 'readonly');
  await expect(surface(page).getByRole('button', { name: 'Export JSONL', exact: true })).toBeEnabled();
  await expect(surface(page).getByRole('button', { name: 'Stage create', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Stage drop', exact: true })).toBeDisabled();
  await expect(surface(page).locator('.document-tabs .n-tabs-tab[data-name="edit"]')).toHaveAttribute('data-disabled', 'true');
  await expect(surface(page).getByRole('button', { name: 'Stage insert', exact: true })).toHaveCount(0);
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: '导入 / 导出', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: 'Stage import', exact: true })).toBeDisabled();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  assertFixtureEvidence(evidence);
});

test('Aggregate preserves user pipeline, appends a 1001 sentinel limit and bounds export and history to 1000', async ({ page }) => {
  const pipeline = [{ $sort: [{ path: '$.score', descending: true }] }, { $limit: 2_000 }];
  const evidence = await prepare(page, { more: true, read: (route, request) => request.action === 'aggregate'
    ? json(route, { collection: collectionName, documents: Array.from({ length: 1_001 }, (_, index) => ({ name: index === 1_000 ? 'AggregateSentinelMustNotExport' : `AggregatePreview:${index}` })), count: 1_001 })
    : defaultRead(route, request, true) });
  await openDocument(page);
  await queryTab(page, 'Aggregate');
  await surface(page).locator('.document-query-editor textarea').fill(JSON.stringify(pipeline));
  await surface(page).getByRole('button', { name: 'Run aggregate', exact: true }).click();
  await expect(page.locator('.workbench-result-panel__alert')).toContainText('结果已截断');
  expect(evidence.reads.at(-1)?.body.pipeline).toEqual([...pipeline, { $limit: 1_001 }]);
  await expect(surface(page).getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  await expect(surface(page).getByTestId('document-preview-budget')).toContainText('1,000');
  await expect(surface(page).locator('.document-query-editor textarea')).toHaveValue(JSON.stringify(pipeline));
  const exported = await exportResultJson(page);
  expect(exported).toHaveLength(1_000);
  expect(JSON.stringify(exported)).not.toContain('AggregateSentinelMustNotExport');
  expect((await historyEntries(page)).find((entry) => entry.action === 'aggregate')).toMatchObject({ rowCount: 1_000, completeness: 'truncated' });
  assertFixtureEvidence(evidence);
});

for (const cap of [7, 1_000]) {
  test(`Distinct bounds the selected ${cap} preview request and keeps over-return exports bounded`, async ({ page }) => {
    const evidence = await prepare(page, { more: true, read: (route, request) => request.action === 'distinct'
      ? json(route, { collection: collectionName, path: '$.site', values: Array.from({ length: cap + 1 }, (_, index) => index === cap ? 'DistinctSentinelMustNotExport' : `DistinctPreview:${index}`) })
      : defaultRead(route, request, true) });
    await openDocument(page);
    await queryTab(page, 'Distinct');
    await surface(page).getByTestId('document-distinct-limit').locator('input').fill(String(cap));
    await surface(page).getByRole('button', { name: 'Run distinct', exact: true }).click();
    await expect(page.locator('.workbench-result-panel__alert')).toContainText('结果已截断');
    expect(evidence.reads.at(-1)).toMatchObject({ action: 'distinct', body: { path: '$.site', limit: Math.min(cap + 1, 1_000) } });
    await expect(surface(page).getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
    const exported = await exportResultJson(page);
    expect(exported).toHaveLength(cap);
    expect(JSON.stringify(exported)).not.toContain('DistinctSentinelMustNotExport');
    expect((await historyEntries(page)).find((entry) => entry.action === 'distinct')).toMatchObject({ rowCount: cap, completeness: 'truncated' });
    assertFixtureEvidence(evidence);
  });
}

test('short Aggregate response is complete and never inherits an earlier Find cursor', async ({ page }) => {
  const evidence = await prepare(page, { more: true, read: (route, request) => request.action === 'aggregate'
    ? json(route, { collection: collectionName, documents: [{ name: 'Short complete aggregate' }], count: 1 })
    : defaultRead(route, request, true) });
  await openDocument(page);
  await queryTab(page, 'Aggregate');
  await surface(page).getByRole('button', { name: 'Run aggregate', exact: true }).click();
  await expect(surface(page)).toHaveAttribute('data-state', 'normal');
  await expect(page.locator('.workbench-result-panel__alert')).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  expect(evidence.reads.at(-1)?.action).toBe('aggregate');
  expect(evidence.reads.at(-1)?.body.continuationToken).toBeUndefined();
  expect((await historyEntries(page)).find((entry) => entry.action === 'aggregate')).toMatchObject({ rowCount: 1, completeness: 'complete' });
  assertFixtureEvidence(evidence);
});

function surface(page: Page) { return page.getByTestId('workbench-document'); }
function recoverButton(page: Page) { return surface(page).getByTestId('document-recover-read-permission'); }

async function openDocument(page: Page): Promise<void> {
  await page.goto(`/admin/app/sql?tool=document&database=${encodeURIComponent(east)}&model=document&node=${encodeURIComponent(collectionName)}`);
  await expect(surface(page)).toHaveAttribute('data-resource-key', collectionName);
}

async function assertState(page: Page, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-state', state);
  await expect(surface(page)).toHaveAttribute('data-database', east);
  await expect(surface(page)).toHaveAttribute('data-resource-key', collectionName);
}

async function assertPermissionPayloadHidden(page: Page): Promise<void> {
  await assertState(page, 'permission');
  await expect(surface(page).locator('.document-grid')).toHaveCount(0);
  await expect(surface(page).locator('.document-json-preview')).toHaveCount(0);
  await expect(page.locator('.workbench-result-panel')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText('Old payload must disappear');
  await expect(surface(page)).not.toContainText('HiddenValidatorPayload');
  await expect(surface(page)).not.toContainText('HiddenIndexPayload');
  await expect(surface(page)).not.toContainText('OldWriteDraftPayload');
  await expect(recoverButton(page)).toBeVisible();
}

async function queryTab(page: Page, tab?: 'Aggregate' | 'Distinct'): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: '查询', exact: true }).click();
  // Naive UI's NTab is a div with data-name, without an ARIA tab role.
  if (tab) await surface(page).locator(`.document-query-editor .n-tabs-tab[data-name="${tab.toLowerCase()}"]`).click();
}

function boundedGate() {
  let release!: () => void;
  const timer = setTimeout(() => release(), 12_000);
  const wait = new Promise<void>((resolve) => { release = () => { clearTimeout(timer); resolve(); }; });
  return { wait, release };
}

async function harness(page: Page, operation: keyof Harness, value?: string | boolean): Promise<void> {
  await page.evaluate(async ({ operation, value }) => {
    const target = (window as HarnessWindow).wb18DocumentHarness;
    if (operation === 'setDatabase') await target.setDatabase(value as string);
    else if (operation === 'setPermission') await target.setPermission(value as boolean);
    else if (operation === 'refreshSchema') await target.refreshSchema();
    else target.unmount();
  }, { operation, value });
}

async function mountComponentHarness(page: Page, options: { readOnly?: boolean; permissionDenied?: boolean } = {}): Promise<void> {
  // Supplies host props that the current routed host does not derive from a
  // verified capability adapter. All query/write actions remain pointer clicks.
  const componentPath = '/src/components/DocumentCollectionWorkbench.vue';
  const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`Document harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, collection, options }) => {
    const [vue, naive, piniaModule, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb18-document-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, collection, collections: [collection], readOnly: Boolean(options.readOnly), permissionDenied: Boolean(options.permissionDenied) });
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, props) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
    (window as HarnessWindow).wb18DocumentHarness = {
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); },
      setPermission: async (value) => { props.permissionDenied = value; await vue.nextTick(); },
      refreshSchema: async () => { props.collection = structuredClone(collection); props.collections = [props.collection]; await vue.nextTick(); },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, collection, options });
}

async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const exportWindow = window as Window & { __wb18Exports?: string[] };
    exportWindow.__wb18Exports = [];
    const originalCreateObjectUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => {
      void value.text().then((content) => { exportWindow.__wb18Exports?.push(content); });
      return originalCreateObjectUrl(value);
    }) as typeof URL.createObjectURL;
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly CONNECTING = 0; readonly OPEN = 1; readonly CLOSED = 2;
      readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {} removeEventListener(): void {}
      dispatchEvent(): boolean { return true; }
      close(): void { this.readyState = 2; }
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database: east });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb18-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [collection], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      const documentPath = `/documents/${collectionName}/`;
      if (suffix.startsWith(documentPath)) {
        const action = suffix.slice(documentPath.length);
        if (['find', 'count', 'aggregate', 'distinct'].includes(action)) {
          const request: ReadRequest = { database, action: action as ReadRequest['action'], body: route.request().postDataJSON() as Record<string, unknown> };
          evidence.reads.push(request);
          if (options.read) return options.read(route, request);
          return defaultRead(route, request, options.more);
        }
        evidence.writes.push(`${route.request().method()} ${path}`);
        return json(route, { code: 'fixture_write_forbidden', message: 'This fixture must not send writes.' }, 501);
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb18_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}

function assertFixtureEvidence(evidence: Evidence): void {
  expect(evidence.writes).toEqual([]);
  expect(evidence.unexpected).toEqual([]);
}

async function defaultRead(route: Route, request: ReadRequest, more = false): Promise<void> {
  if (request.action === 'count') return json(route, { collection: collectionName, count: more ? 2 : 1 });
  if (request.action === 'find') return findResult(route, request.database === west ? 'West document payload' : 'East document payload', more);
  if (request.action === 'aggregate') return json(route, { collection: collectionName, documents: [], count: 0 });
  return json(route, { collection: collectionName, path: '$.site', values: [] });
}

async function findResult(route: Route, value: string, more = false): Promise<void> {
  return json(route, { collection: collectionName, documents: [{ id: 'device-001', document: { name: value, site: 'north' }, version: 2 }], count: 1, limit: 100, skip: 0, continuationToken: more ? 'fixture-cursor' : null, hasMore: more });
}

async function denied(route: Route): Promise<void> { return json(route, { message: 'Document Read permission required' }, 403); }
async function json(route: Route, body: unknown, status = 200): Promise<void> { await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }); }

async function historyEntries(page: Page): Promise<Array<Record<string, unknown>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries);
}

async function exportResultJson(page: Page): Promise<unknown[]> {
  const before = await page.evaluate(() => (window as Window & { __wb18Exports?: string[] }).__wb18Exports?.length ?? 0);
  await page.evaluate(() => {
    window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } }));
  });
  const exportButton = page.locator('.workbench-result-panel').getByTitle('Export result set as JSON', { exact: true });
  await expect(exportButton).toBeVisible();
  await exportButton.click();
  await expect.poll(
    () => page.evaluate((offset) => (window as Window & { __wb18Exports?: string[] }).__wb18Exports?.length ?? 0, before),
    { timeout: 5_000 },
  ).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as Window & { __wb18Exports?: string[] }).__wb18Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('Preview export exceeded the fixture read budget.');
  return JSON.parse(content) as unknown[];
}
