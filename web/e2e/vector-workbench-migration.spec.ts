import { expect, test, type Page, type Route } from '@playwright/test';

// Real routed Vue and a Vite-only prop harness with bounded API fixtures.
// No Server authorization, embedding quality, desktop host, or release claims.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const measurementName = 'DeviceReadings:Main';
const columnName = 'Embedding:Value';
const rawDraft = '[0.25, 0.5, 0.75]';
const textDraft = 'keep this text input';
const filterDraft = "DeviceID = 'draft-device'";
const secret = 'VectorSecretPayloadMustDisappear';
const index = {
  measurement: measurementName, column: columnName, kind: 'hnsw', dimension: 3,
  metric: 'cosine', params: [{ key: 'HiddenIndexParamPayload', value: '16' }], rowCount: 2,
};
const measurement = {
  name: measurementName,
  columns: [
    { name: 'time', role: 'time', dataType: 'TIMESTAMP' },
    { name: 'DeviceID', role: 'tag', dataType: 'STRING' },
    { name: columnName, role: 'field', dataType: 'VECTOR(3)' },
  ],
};

interface SearchRequest { database: string; body: Record<string, unknown> }
interface Evidence {
  searches: SearchRequest[];
  embeds: SearchRequest[];
  sqlReads: SearchRequest[];
  writes: Array<{ database: string; path: string; body: unknown }>;
  unexpected: string[];
}
interface FixtureOptions { search?: (route: Route, request: SearchRequest) => Promise<void> }
interface Harness {
  setDatabase: (database: string) => Promise<void>;
  setReadOnly: (value: boolean) => Promise<void>;
  refreshSchema: () => Promise<void>;
  clearSchema: () => Promise<void>;
  restoreSchema: () => Promise<void>;
  unmount: () => void;
}
type HarnessWindow = Window & { wb20VectorHarness: Harness; __wb20Exports?: string[] };

test.setTimeout(30_000);

test('same vector resource keeps original names and database identity across East and West', async ({ page }) => {
  const evidence = await prepare(page);
  await openVector(page, east);
  await assertIdentity(page, east, 'normal');
  await search(page);
  await expect(surface(page)).toContainText('East vector payload');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await assertIdentity(page, west, 'normal');
  await search(page);
  await expect(surface(page)).toContainText('West vector payload');
  expect(evidence.searches.map((request) => request.database)).toEqual([east, west]);
  expect(evidence.searches.every((request) => request.body.measurement === measurementName && request.body.column === columnName)).toBe(true);
  const route = new URL(page.url());
  expect(route.searchParams.get('model')).toBe('vector');
  expect(route.searchParams.get('node')).toBe(`${measurementName}.${columnName}`);
  assertFixtureEvidence(evidence);
});

for (const status of [401, 403]) {
  test(`HTTP ${status} clears hit metadata and result payload but preserves raw, text and filter input`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { search: (route, request) => deny
      ? json(route, { message: secret }, status)
      : searchResult(route, request.database, secret) });
    await openVector(page, east);
    if (status === 403) await mountHarness(page);
    await queryMode(page, 'text');
    await rawEditor(page).fill(textDraft);
    await queryMode(page, 'raw');
    await rawEditor(page).fill(rawDraft);
    await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
    await surface(page).locator('.vector-filter-row input').fill(filterDraft);
    await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expect(surface(page)).toContainText(secret);
    const previousResult = await openResultDrawer(page);
    await previousResult.locator('.n-tabs-tab[data-name="raw"]').click();
    await expect(previousResult.locator('.sql-result-card__pre')).toContainText(secret);
    await expect(previousResult.locator('.workbench-result-panel__result')).toContainText(secret);
    await previousResult.getByTitle('关闭结果', { exact: true }).click();
    deny = true;
    await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
    await expect(surface(page).locator('.vector-rank-button')).toHaveCount(0);
    await expect(surface(page).locator('.vector-kv-section')).toHaveCount(0);
    await expect(surface(page)).not.toContainText(secret);
    await expect(surface(page)).not.toContainText('HiddenIndexParamPayload');
    await expect(vectorResultPanel(page)).toHaveCount(0);
    await expect(rawEditor(page)).toHaveValue(rawDraft);
    await expect(surface(page).locator('.vector-filter-row input')).toHaveValue(filterDraft);
    await queryMode(page, 'text');
    await expect(rawEditor(page)).toHaveValue(textDraft);
    // A same-identity metadata refresh must not unlock the local deny latch.
    if (status === 403) {
      await harness(page, 'clearSchema');
      await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
      await harness(page, 'restoreSchema');
      await harness(page, 'refreshSchema');
    } else {
      await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click();
    }
    await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
    await queryMode(page, 'raw');
    await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
    expect(evidence.searches).toHaveLength(2);
    assertFixtureEvidence(evidence);
  });
}

test('delayed search cannot populate a different database or an unmounted vector page', async ({ page }) => {
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const stale = 'StaleEastVectorMustNotRender';
  const evidence = await prepare(page, { search: async (route, request) => {
    if (request.database === east) {
      pending += 1;
      await gate.wait;
      try { await searchResult(route, east, stale); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completed += 1; }
    } else await searchResult(route, request.database);
  } });
  try {
    await openVector(page, east);
    await search(page);
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await page.locator('.schema-item--database').filter({ hasText: west }).click();
    await assertIdentity(page, west, 'normal');
    await search(page);
    await expect(surface(page)).toContainText('West vector payload');
    await page.goto(`/admin/app/sql?database=${encodeURIComponent(west)}&tool=sql`);
    await expect(surface(page)).toHaveCount(0);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(page.locator('body')).not.toContainText(stale);
    expect(JSON.stringify(await historyEntries(page))).not.toContain(stale);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('database ABA discards the earlier request even after the original identity returns', async ({ page }) => {
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const stale = 'VectorABAPayloadMustNotRender';
  const evidence = await prepare(page, { search: async (route, request) => {
    if (pending === 0) {
      pending += 1;
      await gate.wait;
      try { await searchResult(route, request.database, stale); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completed += 1; }
    } else await searchResult(route, request.database, 'Current East vector payload');
  } });
  try {
    await openVector(page, east);
    await mountHarness(page);
    await search(page);
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await harness(page, 'setDatabase', west);
    await harness(page, 'setDatabase', east);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(surface(page)).not.toContainText(stale);
    await expect(surface(page).locator('.vector-rank-button')).toHaveCount(0);
    await search(page);
    await expect(surface(page)).toContainText('Current East vector payload');
    expect(JSON.stringify(await historyEntries(page))).not.toContain(stale);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('Top-K and filter stay bound to the dispatched request and oversized hits are capped before export', async ({ page }) => {
  let pending = 0;
  const gate = boundedGate();
  const sentinel = 'VectorSentinelMustNotExport';
  const evidence = await prepare(page, { search: async (route) => {
    pending += 1;
    await gate.wait;
    await json(route, { hits: Array.from({ length: 101 }, (_, number) => ({
      timestampUtc: 1_780_000_000_000 + number, distance: number / 1000,
      tags: [{ key: 'DeviceID', value: 'bounded' }],
      fields: [{ key: 'Preview', value: number === 100 ? sentinel : `VectorPreview:${number}` }],
    })) });
  } });
  try {
    await openVector(page, east);
    await surface(page).locator('.vector-toolbar__topk input').fill('100');
    await surface(page).locator('.vector-filter-row input').fill(filterDraft);
    await search(page);
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await surface(page).locator('.vector-toolbar__topk input').fill('7');
    await surface(page).locator('.vector-filter-row input').fill("DeviceID = 'later-input'");
    gate.release();
    const panel = await openResultDrawer(page);
    await expect(panel.locator('.workbench-result-panel__alert')).toContainText(/截断|truncated/u);
    await expect(surface(page)).not.toContainText(sentinel);
    expect(evidence.searches[0].body).toMatchObject({ topK: 100, filter: filterDraft });
    const exported = await exportResultJson(page);
    expect(exported).toHaveLength(100);
    expect(JSON.stringify(exported)).not.toContain(sentinel);
    const entry = (await historyEntries(page)).find((item) => item.action === 'search');
    expect(entry).toMatchObject({ database: east, rowCount: 100, completeness: 'truncated' });
    expect(String(entry?.command)).toContain('100');
    expect(String(entry?.command)).toContain(filterDraft);
    expect(String(entry?.command)).not.toContain('later-input');
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('dimension mismatch and non-finite or coerced JSON components do not dispatch search', async ({ page }) => {
  const evidence = await prepare(page);
  await openVector(page, east);
  await rawEditor(page).fill('[1, 2]');
  await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
  await expect(surface(page)).toContainText(/expected 3|维度/u);
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  await rawEditor(page).fill('[1e309, 0, 0]');
  await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  await rawEditor(page).fill('["1", null, true]');
  await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  expect(evidence.searches).toEqual([]);
  assertFixtureEvidence(evidence);
});

test('Text embed without an explicit index Profile shows its boundary and never calls embed-preview', async ({ page }) => {
  const evidence = await prepare(page);
  await openVector(page, east);
  await queryMode(page, 'text');
  await rawEditor(page).fill(textDraft);
  await expect(surface(page)).toContainText(/Profile/u);
  await expect(surface(page).getByRole('button', { name: 'Embed text', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  expect(evidence.embeds).toEqual([]);
  await queryMode(page, 'raw');
  await search(page);
  await expect(surface(page)).toContainText('East vector payload');
  assertFixtureEvidence(evidence);
});

test('Measurement import is staged locally and old approval and draft are discarded on context switch', async ({ page }) => {
  const evidence = await prepare(page);
  await openVector(page, east);
  await mountHarness(page);
  await vectorTab(page, '数据编辑 / 导入');
  const child = surface(page).getByTestId('workbench-measurement');
  await expect(child).toHaveAttribute('data-database', east);
  await child.locator('.workbench-section-tabs').getByRole('button', { name: '文件导入', exact: true }).click();
  await child.getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据').fill(`time,DeviceID,${columnName}\n1780000000000,OldImportDraft,"[0.1,0.2,0.3]"`);
  await child.getByRole('button', { name: '解析', exact: true }).click();
  await child.getByRole('button', { name: '暂存导入', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Measurement import' });
  await expect(approval).toContainText(measurementName);
  // Do not confirm: all writes would be recorded and rejected by this fixture.
  await harness(page, 'setDatabase', west);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await vectorTab(page, '数据编辑 / 导入');
  await expect(surface(page).getByTestId('workbench-measurement')).toHaveAttribute('data-database', west);
  await surface(page).getByTestId('workbench-measurement').locator('.workbench-section-tabs').getByRole('button', { name: '文件导入', exact: true }).click();
  await expect(surface(page).getByTestId('workbench-measurement').getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据')).toHaveValue('');
  expect(evidence.sqlReads.length).toBeGreaterThan(0);
  assertFixtureEvidence(evidence);
});

test('readonly host allows raw search and Measurement reads while rejecting child writes', async ({ page }) => {
  const evidence = await prepare(page);
  await openVector(page, east);
  await mountHarness(page, { readOnly: true });
  await assertIdentity(page, east, 'readonly');
  await search(page);
  await expect(surface(page)).toContainText('East vector payload');
  await vectorTab(page, '数据编辑 / 导入');
  const child = surface(page).getByTestId('workbench-measurement');
  await expect(child).toHaveAttribute('data-state', 'readonly');
  await expect(child.getByRole('button', { name: '新增数据点', exact: true })).toBeDisabled();
  await child.locator('.workbench-section-tabs').getByRole('button', { name: '文件导入', exact: true }).click();
  await expect(child.getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据')).toBeDisabled();
  await expect(child.getByRole('button', { name: '暂存导入', exact: true })).toBeDisabled();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  assertFixtureEvidence(evidence);
});

function surface(page: Page) { return page.getByTestId('workbench-vector'); }
function rawEditor(page: Page) { return surface(page).locator('.vector-query-editor textarea'); }
async function queryMode(page: Page, mode: 'raw' | 'text'): Promise<void> {
  await surface(page).locator(`.vector-query-editor .n-tabs-tab[data-name="${mode}"]`).click();
}
async function vectorTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').first().getByRole('button', { name, exact: true }).click();
}
async function openVector(page: Page, database: string): Promise<void> {
  await page.goto(`/admin/app/sql?tool=vector&database=${encodeURIComponent(database)}&model=vector&node=${encodeURIComponent(`${measurementName}.${columnName}`)}`);
  await expect(surface(page)).toHaveAttribute('data-database', database);
}
async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `${measurementName}:${columnName}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
  await expect(surface(page)).toContainText(`${measurementName}.${columnName}`);
}
async function search(page: Page): Promise<void> {
  await rawEditor(page).fill(rawDraft);
  await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
  await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
}
function boundedGate() {
  let release!: () => void;
  let released = false;
  const timer = setTimeout(() => release(), 10_000);
  const wait = new Promise<void>((resolve) => { release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } }; });
  return { wait, release };
}
async function harness(page: Page, operation: keyof Harness, value?: string | boolean): Promise<void> {
  await page.evaluate(async ({ operation, value }) => {
    const target = (window as HarnessWindow).wb20VectorHarness;
    if (operation === 'setDatabase') await target.setDatabase(value as string);
    else if (operation === 'setReadOnly') await target.setReadOnly(value as boolean);
    else if (operation === 'refreshSchema') await target.refreshSchema();
    else if (operation === 'clearSchema') await target.clearSchema();
    else if (operation === 'restoreSchema') await target.restoreSchema();
    else target.unmount();
  }, { operation, value });
}
async function mountHarness(page: Page, options: { readOnly?: boolean } = {}): Promise<void> {
  const componentPath = '/src/components/VectorSearchWorkbench.vue';
  const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`Vector harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, index, measurement, options }) => {
    const [vue, naive, piniaModule, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb20-vector-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, index, indexes: [index], measurement, readOnly: Boolean(options.readOnly) });
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, props) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
    (window as HarnessWindow).wb20VectorHarness = {
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); },
      setReadOnly: async (value) => { props.readOnly = value; await vue.nextTick(); },
      refreshSchema: async () => { props.index = structuredClone(index); props.indexes = [props.index]; await vue.nextTick(); },
      clearSchema: async () => { props.index = null; props.indexes = []; await vue.nextTick(); },
      restoreSchema: async () => { props.index = structuredClone(index); props.indexes = [props.index]; await vue.nextTick(); },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, index, measurement, options });
}
async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { searches: [], embeds: [], sqlReads: [], writes: [], unexpected: [] };
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const exportWindow = window as HarnessWindow;
    exportWindow.__wb20Exports = [];
    const createUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => { void value.text().then((content) => exportWindow.__wb20Exports?.push(content)); return createUrl(value); }) as typeof URL.createObjectURL;
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {} removeEventListener(): void {} dispatchEvent(): boolean { return true; } close(): void {}
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database: east });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb20-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [measurement], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/vector/indexes') return json(route, { indexes: [index] });
      if (suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      if (suffix === '/vector/search-preview') {
        const request = { database, body: route.request().postDataJSON() as Record<string, unknown> };
        evidence.searches.push(request);
        return options.search ? options.search(route, request) : searchResult(route, database);
      }
      if (suffix === '/vector/embed-preview') {
        evidence.embeds.push({ database, body: route.request().postDataJSON() as Record<string, unknown> });
        return json(route, { code: 'fixture_implicit_embedding_forbidden' }, 501);
      }
      if (suffix === '/sql') {
        const request = { database, body: route.request().postDataJSON() as Record<string, unknown> };
        if (/^\s*SELECT\b/iu.test(String(request.body.sql))) {
          evidence.sqlReads.push(request);
          return route.fulfill({ status: 200, contentType: 'application/x-ndjson', body: [
            JSON.stringify({ type: 'meta', columns: ['time', 'DeviceID', columnName] }),
            JSON.stringify([1_780_000_000_000, 'fixture-device', [0.1, 0.2, 0.3]]),
            JSON.stringify({ type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 1 }),
          ].join('\n') });
        }
      }
      if (route.request().method() !== 'GET') {
        evidence.writes.push({ database, path: suffix, body: route.request().postDataJSON() });
        return json(route, { code: 'wb20_fixture_write_forbidden', message: 'Staging only: writes are recorded and rejected.' }, 501);
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb20_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}
async function searchResult(route: Route, database: string, payload = `${database === east ? 'East' : 'West'} vector payload`): Promise<void> {
  await json(route, { hits: [{ timestampUtc: 1_780_000_000_000, distance: 0.123,
    tags: [{ key: 'DeviceID', value: payload }], fields: [{ key: 'Preview', value: payload }],
  }] });
}
async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
function assertFixtureEvidence(evidence: Evidence): void {
  expect(evidence.writes).toEqual([]);
  expect(evidence.embeds).toEqual([]);
  expect(evidence.unexpected).toEqual([]);
}
async function historyEntries(page: Page): Promise<Array<Record<string, unknown>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries);
}
async function exportResultJson(page: Page): Promise<unknown[]> {
  const before = await page.evaluate(() => (window as HarnessWindow).__wb20Exports?.length ?? 0);
  const panel = await openResultDrawer(page);
  const button = panel.getByTitle('Export result set as JSON', { exact: true });
  await expect(button).toBeVisible();
  await button.click();
  await expect.poll(() => page.evaluate(() => (window as HarnessWindow).__wb20Exports?.length ?? 0), { timeout: 5_000 }).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as HarnessWindow).__wb20Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('Vector export exceeded the fixture read budget.');
  return JSON.parse(content) as unknown[];
}

function vectorResultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({
    has: page.locator('.workbench-result-panel__title').filter({ hasText: /^Vector search result$/u }),
  });
}
async function openResultDrawer(page: Page) {
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } })));
  const panel = vectorResultPanel(page);
  await expect(panel).toBeVisible();
  return panel;
}
