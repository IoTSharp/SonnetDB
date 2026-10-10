import { expect, test, type Page, type Route } from '@playwright/test';

// Routed Vue plus a Vite-only prop harness; authentication and every API are
// fixtures. This is not Server authorization or a desktop/release gate.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const keyspace = 'DeviceState:Main';
const keyName = 'Device:Original';
const secret = 'OldKvPayloadMustDisappear';
const writeDraft = 'OldWriteDraftMustDisappear';

interface ReadRequest { database: string; action: 'scan' | 'stats' | 'get-many'; body: Record<string, unknown> }
interface WriteRequest { database: string; action: string; body: Record<string, unknown> }
interface Evidence { reads: ReadRequest[]; writes: WriteRequest[]; unexpected: string[] }
interface FixtureOptions {
  read?: (route: Route, request: ReadRequest) => Promise<void>;
  write?: (route: Route, request: WriteRequest) => Promise<void>;
}
interface Harness {
  setDatabase: (value: string) => Promise<void>;
  clearResource: () => Promise<void>;
  restoreResource: () => Promise<void>;
  refreshResource: () => Promise<void>;
  unmount: () => void;
}
type FixtureWindow = Window & { wb21KvHarness: Harness; __wb21Exports?: string[] };

test.setTimeout(30_000);

test('same keyspace preserves database, original case and colon names across East and West', async ({ page }) => {
  const evidence = await prepare(page);
  await openKv(page, east);
  await assertIdentity(page, east, 'normal');
  await expect(surface(page)).toContainText('East KV payload');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await assertIdentity(page, west, 'normal');
  await expect(surface(page)).toContainText('West KV payload');
  expect(evidence.reads.some((item) => item.database === east && item.action === 'scan')).toBe(true);
  expect(evidence.reads.some((item) => item.database === west && item.action === 'scan')).toBe(true);
  expect(new URL(page.url()).searchParams.get('node')).toBe(keyspace);
  assertFixtureEvidence(evidence);
});

for (const status of [401, 403]) {
  test(`Scan HTTP ${status} removes values/results/drafts and remains locked through same-identity refresh`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.action === 'scan'
      ? json(route, { message: secret }, status)
      : defaultRead(route, request, secret) });
    await openKv(page);
    if (status === 403) await mountHarness(page);
    await expect(surface(page).locator('.kv-value-preview')).toContainText(secret);
    const result = await openResult(page);
    await result.locator('.n-tabs-tab[data-name="raw"]').click();
    await expect(result.locator('.workbench-result-panel__result')).toContainText(secret);
    await result.getByTitle('关闭结果', { exact: true }).click();
    await batchTab(page);
    await fillEditor(page, writeDraft);
    await surface(page).getByPlaceholder('Batch set, one key=value per line').fill(`DraftKey=${writeDraft}`);
    deny = true;
    await surface(page).getByRole('button', { name: 'Scan', exact: true }).click();
    await assertPermissionHidden(page);
    if (status === 403) {
      const deniedReadCount = evidence.reads.length;
      await harness(page, 'setDatabase', '');
      await expect(surface(page)).toHaveAttribute('data-database', '');
      await assertPermissionHidden(page);
      await harness(page, 'setDatabase', east);
      await expect(surface(page)).toHaveAttribute('data-database', east);
      await assertPermissionHidden(page);
      await harness(page, 'clearResource');
      await assertPermissionHidden(page);
      await harness(page, 'restoreResource');
      await assertPermissionHidden(page);
      await harness(page, 'refreshResource');
      await assertPermissionHidden(page);
      expect(evidence.reads).toHaveLength(deniedReadCount);
    }
    await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
    await expect(surface(page).getByRole('button', { name: 'Scan', exact: true })).toBeDisabled();
    assertFixtureEvidence(evidence);
  });
}

test('Get-many 403 clears earlier Scan values, its result and editing draft', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => request.action === 'get-many'
    ? json(route, { message: secret }, 403) : defaultRead(route, request, secret) });
  await openKv(page);
  await expect(surface(page).locator('.kv-value-preview')).toContainText(secret);
  await batchTab(page);
  await fillEditor(page, writeDraft);
  await surface(page).getByPlaceholder('Keys, one per line').fill(keyName);
  await surface(page).getByRole('button', { name: 'Batch get', exact: true }).click();
  await assertPermissionHidden(page);
  expect(evidence.reads.filter((item) => item.action === 'get-many')).toHaveLength(1);
  assertFixtureEvidence(evidence);
});

test('Stats 403 clears old statistics, values and draft while a concurrent refresh cannot refill them', async ({ page }) => {
  let deny = false;
  let pendingScan = 0;
  let completedScan = 0;
  const deniedStats = boundedGate();
  const delayedScan = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (deny && request.action === 'stats') {
      await deniedStats.wait;
      return json(route, { message: secret }, 403);
    }
    if (deny && request.action === 'scan') {
      pendingScan += 1;
      await delayedScan.wait;
      try { await defaultRead(route, request, secret); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completedScan += 1; }
      return;
    }
    await defaultRead(route, request, secret);
  } });
  try {
    await openKv(page);
    await expect(surface(page).locator('.kv-value-preview')).toContainText(secret);
    await batchTab(page);
    await fillEditor(page, writeDraft);
    await kvTab(page, '统计');
    await expect(surface(page).locator('.kv-stats')).toContainText('4,242');
    deny = true;
    await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect.poll(() => pendingScan, { timeout: 5_000 }).toBe(1);
    deniedStats.release();
    await assertPermissionHidden(page);
    delayedScan.release();
    await expect.poll(() => completedScan, { timeout: 5_000 }).toBe(1);
    await assertPermissionHidden(page);
    assertFixtureEvidence(evidence);
  } finally { deniedStats.release(); delayedScan.release(); }
});

test('a fixture write denial consumes approval and removes all old value and write draft payload', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => defaultRead(route, request, secret),
    write: (route) => json(route, { message: secret }, 403) });
  await openKv(page);
  await expect(surface(page).locator('.kv-value-preview')).toContainText(secret);
  await batchTab(page);
  await fillEditor(page, writeDraft);
  await surface(page).getByRole('button', { name: 'Stage set', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'KV operation batch' });
  await expect(approval).toContainText(keyspace);
  // This case explicitly dispatches to the intercepted fixture only. No
  // request can escape the fixture handler or reach a real Server.
  await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await assertPermissionHidden(page);
  expect(evidence.writes).toHaveLength(1);
  expect(evidence.writes[0]).toMatchObject({ database: east, action: 'set-conditional' });
  assertFixtureEvidence(evidence, 1);
});

test('readonly prop host preserves Scan, Get and round-trip export while rejecting import, writes and TTL', async ({ page }) => {
  const evidence = await prepare(page);
  await openKv(page);
  await mountHarness(page, { readOnly: true });
  await assertIdentity(page, east, 'readonly');
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
  const exported = await roundTripExport(page);
  expect(exported).toHaveLength(1);
  await expect(surface(page).getByRole('button', { name: '导入文件', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Remove', exact: true })).toBeDisabled();
  await batchTab(page);
  await expect(surface(page).locator('.kv-editor')).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Stage expire', exact: true })).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Stage persist', exact: true })).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Batch remove', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Stage batch set', exact: true })).toBeDisabled();
  await surface(page).getByPlaceholder('Keys, one per line').fill(keyName);
  await surface(page).getByRole('button', { name: 'Batch get', exact: true }).click();
  const panel = await openResult(page);
  await panel.locator('.n-tabs-tab[data-name="raw"]').click();
  await expect(panel.locator('.workbench-result-panel__result')).toContainText('East KV payload');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(evidence.reads.some((item) => item.action === 'get-many')).toBe(true);
  assertFixtureEvidence(evidence);
});

test('late Scan response cannot cross database switch or refill a page after unmount', async ({ page }) => {
  let delay = false;
  let pending = 0;
  let completed = 0;
  const stale = 'StaleEastKvMustNotRender';
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (delay && request.database === east && request.action === 'scan') {
      pending += 1;
      await gate.wait;
      try { await defaultRead(route, request, stale); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completed += 1; }
    } else await defaultRead(route, request);
  } });
  try {
    await openKv(page);
    await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
    delay = true;
    await surface(page).getByRole('button', { name: 'Scan', exact: true }).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await page.locator('.schema-item--database').filter({ hasText: west }).click();
    await expect(surface(page)).toHaveAttribute('data-database', west);
    await expect(surface(page)).toContainText('West KV payload');
    await page.goto(`/admin/app/sql?tool=sql&database=${encodeURIComponent(west)}`);
    await expect(surface(page)).toHaveCount(0);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(page.locator('body')).not.toContainText(stale);
    expect(JSON.stringify(await historyEntries(page))).not.toContain(stale);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('database ABA invalidates a delayed Scan even when the original keyspace identity returns', async ({ page }) => {
  let delay = false;
  let pending = 0;
  let completed = 0;
  const stale = 'KvABAPayloadMustNotRender';
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (delay && pending === 0 && request.action === 'scan') {
      pending += 1;
      await gate.wait;
      try { await defaultRead(route, request, stale); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completed += 1; }
    } else await defaultRead(route, request);
  } });
  try {
    await openKv(page);
    await mountHarness(page);
    await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
    delay = true;
    await surface(page).getByRole('button', { name: 'Scan', exact: true }).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await harness(page, 'setDatabase', west);
    await harness(page, 'setDatabase', east);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(surface(page)).not.toContainText(stale);
    expect(JSON.stringify(await historyEntries(page))).not.toContain(stale);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('fixture cursor pagination caps accumulated preview at 1000 and rejects an overreturned cursor', async ({ page }) => {
  const sentinel = 'KvCursorSentinelMustNotExport';
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action !== 'scan') return defaultRead(route, request);
    if (request.body.cursor === 'cursor:600') return json(route, {
      entries: Array.from({ length: 500 }, (_, number) => entry(`Key:${number + 600}`, number >= 400 ? sentinel : `Row:${number + 600}`)),
      nextCursor: 'cursor:1100-skips-unretained', hasMore: true,
    });
    if (request.body.limit === 1000) return json(route, {
      entries: Array.from({ length: 600 }, (_, number) => entry(`Key:${number}`, `Row:${number}`)),
      nextCursor: 'cursor:600', hasMore: true,
    });
    return defaultRead(route, request);
  } });
  await openKv(page);
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
  await surface(page).locator('.kv-toolbar__limit').click();
  await page.getByText('1000 keys', { exact: true }).click();
  await surface(page).getByRole('button', { name: 'Scan', exact: true }).click();
  await expect(surface(page).locator('.kv-pager')).toContainText('600');
  await surface(page).getByRole('button', { name: 'Load more', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: 'Load more', exact: true })).toBeDisabled();
  await expect(surface(page).getByTestId('kv-preview-budget')).toContainText(/truncated|截断/u);
  const second = evidence.reads.find((request) => request.action === 'scan' && request.body.cursor === 'cursor:600');
  expect(second?.body.limit).toBe(400);
  const exported = await roundTripExport(page);
  expect(exported).toHaveLength(1000);
  expect(JSON.stringify(exported)).not.toContain(sentinel);
  expect(evidence.reads.filter((request) => request.body.cursor === 'cursor:1100-skips-unretained')).toEqual([]);
  const previewHistory = (await historyEntries(page)).filter((item) => item.model === 'kv' && item.rowCount === 1000);
  expect(previewHistory.some((item) => item.completeness === 'truncated')).toBe(true);
  // Keep the full preview/export while mounting only the visible table rows.
  const renderedKeys = await surface(page).locator('.kv-key-button').count();
  expect(renderedKeys).toBeGreaterThan(0);
  expect(renderedKeys).toBeLessThan(100);
  await surface(page).locator('.kv-grid .v-vl').evaluate((element) => { element.scrollTop = element.scrollHeight; });
  const lastKey = surface(page).getByRole('button', { name: 'Key:999', exact: true });
  await expect(lastKey).toBeVisible();
  await lastKey.click();
  await expect(surface(page).locator('.kv-value-preview')).toHaveText('Row:999');
  const selected = surface(page).getByRole('row').filter({ has: page.getByRole('button', { name: 'Key:999', exact: true }) }).getByRole('checkbox');
  await selected.click();
  await expect(selected).toHaveAttribute('aria-checked', 'true');
  await surface(page).getByPlaceholder('Filter loaded keys').fill('Key:0');
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
  const selectedExport = await roundTripExport(page);
  expect(selectedExport).toHaveLength(1);
  expect(selectedExport[0].key).toBe('Key:999');
  expect(Buffer.from(String(selectedExport[0].valueBase64), 'base64').toString()).toBe('Row:999');
  assertFixtureEvidence(evidence);
});

test('4096-byte Text/JSON/Hex/Base64 previews stay bounded and full JSONL export never prefills a truncated draft', async ({ page }) => {
  const tail = 'LongKvTailMustRemainOnlyInFullExport';
  const value = JSON.stringify({ text: 'L'.repeat(8192), tail });
  const base64 = Buffer.from(value).toString('base64');
  const evidence = await prepare(page, { read: (route, request) => request.action === 'scan'
    ? json(route, { entries: [{ key: keyName, value: base64, version: 1, expiresAtUtc: null }], nextCursor: null, hasMore: false })
    : defaultRead(route, request) });
  await openKv(page);
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(1);
  await surface(page).locator('.kv-key-button').click();
  await expect(surface(page).getByTestId('kv-value-budget')).toContainText(/4096|4,096/u);
  await expect(surface(page).getByTestId('kv-value-budget')).toContainText(/truncated|截断/u);
  const details = await surface(page).locator('.kv-detail-strip').innerText();
  expect(details.replaceAll(',', '')).toContain(`${Buffer.byteLength(value)} bytes`);
  // Four fixed format actions, each bounded by the source-byte budget; no
  // reproduction of the component's decoding/formatting implementation.
  for (const format of ['text', 'json', 'hex', 'base64']) {
    await surface(page).locator(`.kv-value-tabs .n-tabs-tab[data-name="${format}"]`).click();
    const preview = await surface(page).locator('.kv-value-preview').innerText();
    expect(preview.length).toBeLessThanOrEqual(format === 'hex' ? 12_288 : format === 'base64' ? 5_464 : 4_096);
    expect(preview).not.toContain(tail);
  }
  const exported = await roundTripExport(page);
  expect(exported).toHaveLength(1);
  expect(exported[0].valueBase64).toBe(base64);
  expect(Buffer.from(String(exported[0].valueBase64), 'base64').toString()).toBe(value);
  await batchTab(page);
  await expect(surface(page).getByPlaceholder('Value', { exact: true })).toHaveValue('');
  assertFixtureEvidence(evidence);
});

test('unknown fixture write stays unknown in history and its consumed approval is never replayed', async ({ page }) => {
  const evidence = await prepare(page, { write: (route) => json(route, { message: 'Fixture response lost after dispatch' }, 503) });
  await openKv(page);
  await batchTab(page);
  await fillEditor(page, 'UnknownOutcomeDraft');
  await surface(page).getByRole('button', { name: 'Stage set', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'KV operation batch' });
  await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).toContainText(/未知|待核对/u);
  await expect.poll(async () => (await historyEntries(page)).find((item) => item.title === 'KV operation batch')?.status, { timeout: 5_000 }).toBe('unknown');
  expect(evidence.writes).toHaveLength(1);
  await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).not.toHaveClass(/n-button--loading/u);
  expect(evidence.writes).toHaveLength(1);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  assertFixtureEvidence(evidence, 1);
});

function surface(page: Page) { return page.getByTestId('workbench-kv'); }
function resultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({ has: page.locator('.workbench-result-panel__title').filter({ hasText: /^KV operation result$/u }) });
}
async function openResult(page: Page) {
  await surface(page).getByRole('button', { name: '查看 KV 结果', exact: true }).click();
  const panel = resultPanel(page);
  await expect(panel).toBeVisible();
  return panel;
}
async function openKv(page: Page, database = east): Promise<void> {
  await page.goto(`/admin/app/sql?tool=kv&database=${encodeURIComponent(database)}&model=kv&node=${encodeURIComponent(keyspace)}`);
  await expect(surface(page)).toHaveAttribute('data-database', database);
}
async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `kv:${keyspace}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
}
async function assertPermissionHidden(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(0);
  await expect(surface(page).locator('.kv-value-preview')).toHaveCount(0);
  await expect(surface(page).locator('.kv-editor')).toHaveCount(0);
  await expect(surface(page).locator('.kv-stats')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(secret);
  await expect(surface(page)).not.toContainText(writeDraft);
}
async function kvTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function batchTab(page: Page): Promise<void> { await kvTab(page, '批量操作'); }
async function fillEditor(page: Page, value: string): Promise<void> {
  await surface(page).getByPlaceholder('Key', { exact: true }).fill('OldDraftKey');
  await surface(page).getByPlaceholder('Value', { exact: true }).fill(value);
}
function boundedGate() {
  let release!: () => void;
  let released = false;
  const timer = setTimeout(() => release(), 10_000);
  const wait = new Promise<void>((resolve) => { release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } }; });
  return { wait, release };
}
async function harness(page: Page, operation: keyof Harness, value?: string): Promise<void> {
  await page.evaluate(async ({ operation, value }) => {
    const target = (window as FixtureWindow).wb21KvHarness;
    if (operation === 'setDatabase') await target.setDatabase(value as string);
    else if (operation === 'clearResource') await target.clearResource();
    else if (operation === 'restoreResource') await target.restoreResource();
    else if (operation === 'refreshResource') await target.refreshResource();
    else target.unmount();
  }, { operation, value });
}
async function mountHarness(page: Page, options: { readOnly?: boolean } = {}): Promise<void> {
  const componentPath = '/src/components/KvKeyspaceWorkbench.vue';
  const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`KV harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, keyspace, options }) => {
    const [vue, naive, piniaModule, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb21-kv-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, keyspace, keyspaces: [keyspace], readOnly: Boolean(options.readOnly) });
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, props) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
    (window as FixtureWindow).wb21KvHarness = {
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); },
      clearResource: async () => { props.keyspace = ''; props.keyspaces = []; await vue.nextTick(); },
      restoreResource: async () => { props.keyspace = keyspace; props.keyspaces = [keyspace]; await vue.nextTick(); },
      refreshResource: async () => { props.keyspaces = [keyspace]; await vue.nextTick(); },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, keyspace, options });
}
async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const fixture = window as FixtureWindow;
    fixture.__wb21Exports = [];
    const createUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => { void value.text().then((content) => fixture.__wb21Exports?.push(content)); return createUrl(value); }) as typeof URL.createObjectURL;
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
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb21-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [keyspace] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      const prefix = `/kv/${keyspace}/`;
      if (suffix.startsWith(prefix)) {
        const action = suffix.slice(prefix.length);
        const body = route.request().postDataJSON() as Record<string, unknown> | null;
        if (['scan', 'stats', 'get-many'].includes(action)) {
          const request = { database, action: action as ReadRequest['action'], body: body ?? {} };
          evidence.reads.push(request);
          return options.read ? options.read(route, request) : defaultRead(route, request);
        }
        const request = { database, action, body: body ?? {} };
        evidence.writes.push(request);
        return options.write ? options.write(route, request) : json(route, { code: 'wb21_fixture_write_forbidden' }, 501);
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb21_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}
function entry(key: string, value: string) { return { key, value: Buffer.from(value).toString('base64'), version: 1, expiresAtUtc: null }; }
async function defaultRead(route: Route, request: ReadRequest, payload = `${request.database === east ? 'East' : 'West'} KV payload`): Promise<void> {
  if (request.action === 'stats') return json(route, { totalKeys: 4242, activeKeys: 4242, expiredKeys: 0, expiringKeys: 0, nearestExpiresAtUtc: null });
  if (request.action === 'scan') return json(route, { entries: [entry(keyName, payload)], nextCursor: null, hasMore: false });
  return json(route, { values: (request.body.keys as string[] ?? [keyName]).map((key) => ({ ...entry(key, payload), found: true })) });
}
async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
function assertFixtureEvidence(evidence: Evidence, expectedWrites = 0): void {
  expect(evidence.writes).toHaveLength(expectedWrites);
  expect(evidence.unexpected).toEqual([]);
}
async function historyEntries(page: Page): Promise<Array<Record<string, unknown>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries);
}
async function roundTripExport(page: Page): Promise<Array<Record<string, unknown>>> {
  const before = await page.evaluate(() => (window as FixtureWindow).__wb21Exports?.length ?? 0);
  await surface(page).getByRole('button', { name: '导出 round-trip', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as FixtureWindow).__wb21Exports?.length ?? 0), { timeout: 5_000 }).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as FixtureWindow).__wb21Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('KV export exceeded the fixture read budget.');
  const lines = content.trim().split(/\r?\n/u);
  if (lines.length > 1000) throw new Error('KV export exceeded the preview row budget.');
  return lines.map((line) => JSON.parse(line) as Record<string, unknown>);
}
