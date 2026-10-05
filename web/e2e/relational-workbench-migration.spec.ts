import { expect, test, type Page, type Route } from '@playwright/test';

// Real Web routing and components with API fixtures: this suite does not claim
// Server authorization, a real table snapshot, or a Studio/VS Code host run.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const tableName = 'DeviceID:Main';
const draftSql = 'SELECT * FROM WorkbenchDraftMustNotRun;';
const historySql = 'DELETE FROM HistoryDraftMustNotRun;';
const createdUtc = '2026-10-06T00:00:00Z';
const columns = [
  { name: 'DeviceID', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 },
  { name: 'MixedCase:Name', dataType: 'STRING', isPrimaryKey: false, isNullable: false, ordinal: 1 },
  { name: 'Optional:Note', dataType: 'STRING', isPrimaryKey: false, isNullable: true, ordinal: 2 },
];
const table = { name: tableName, columns, primaryKey: ['DeviceID'], indexes: [], foreignKeys: [], createdUtc };

interface SqlRequest {
  database: string;
  sql: string;
  parameters?: Record<string, { integerValue?: number; stringValue?: string }>;
  previewMaxRows?: number;
}

interface Evidence {
  reads: SqlRequest[];
  writes: Array<{ database: string; statements: Array<{ sql: string; parameters?: unknown }> }>;
  unexpected: string[];
}

interface FixtureOptions {
  sql?: (route: Route, request: SqlRequest) => Promise<void>;
  batch?: (route: Route, request: Evidence['writes'][number]) => Promise<void>;
}

test.setTimeout(30_000);

test('normal table preview retains database and original mixed-case colon identity', async ({ page }) => {
  const evidence = await prepare(page);
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await expect(page.locator('.relation-grid')).toContainText('East visible');
  await expect(page.locator('.schema-item--table.is-active strong')).toHaveText(tableName);
  await expect(page.locator('.workspace-tab[aria-selected="true"]')).toContainText(tableName);
  expect(evidence.reads.length).toBeGreaterThan(0);
  for (const request of evidence.reads) {
    expect(request.database).toBe(east);
    expect(request.sql).toContain('FROM "DeviceID:Main"');
    expect(request.sql).toContain('"MixedCase:Name"');
    expect(request.sql).toMatch(/LIMIT @limit\s+OFFSET @offset/iu);
    expect(request.parameters?.limit?.integerValue).toBe(50);
    expect(request.parameters?.offset?.integerValue).toBe(0);
  }
  assertReadOnlyEvidence(evidence);
});

test('empty filtered results preserve table identity and filter inputs', async ({ page }) => {
  const evidence = await prepare(page, {
    sql: (route, request) => rows(route, request.parameters?.filter_text ? [] : [[1, 'East visible', null]]),
  });
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await page.getByPlaceholder('Filter', { exact: true }).fill('Missing:Asset');
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await assertIdentity(page, east, 'empty');
  await expect(page.getByPlaceholder('Filter', { exact: true })).toHaveValue('Missing:Asset');
  await expect(page.locator('.relation-grid-shell')).not.toContainText('East visible');
  expect(evidence.reads.at(-1)?.parameters?.filter_text?.stringValue).toBe('%Missing:Asset%');
  assertReadOnlyEvidence(evidence);
});

test('read failure replaces old rows and allows an explicit bounded retry', async ({ page }) => {
  let fail = false;
  const evidence = await prepare(page, {
    sql: (route) => fail ? json(route, { code: 'fixture_read_error', message: 'Fixture read failed' }, 503)
      : rows(route, [[1, 'East visible', null]]),
  });
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await page.getByPlaceholder('Filter', { exact: true }).fill('Retained draft filter');
  fail = true;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await assertIdentity(page, east, 'error');
  await expect(page.getByPlaceholder('Filter', { exact: true })).toHaveValue('Retained draft filter');
  await expect(page.locator('.relation-grid-shell')).not.toContainText('East visible');
  await expect(page.getByTestId('workbench-table')).toContainText('Fixture read failed');
  fail = false;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await assertIdentity(page, east, 'normal');
  assertReadOnlyEvidence(evidence);
});

test('HTTP 403 clears old rows, result payload and row draft and blocks further reads', async ({ page }) => {
  let deny = false;
  const evidence = await prepare(page, {
    sql: (route) => deny ? json(route, { message: 'Database table Read permission required' }, 403)
      : rows(route, [[1, 'Permission payload must disappear', 'Hidden field payload']]),
  });
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await page.getByRole('button', { name: 'Insert row', exact: true }).click();
  await expect(page.locator('.relation-insert')).toBeVisible();
  deny = true;
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await assertIdentity(page, east, 'permission');
  await expect(page.locator('.relation-insert')).toHaveCount(0);
  await expect(page.getByTestId('workbench-table')).not.toContainText('Permission payload must disappear');
  await expect(page.getByTestId('workbench-table')).not.toContainText('Hidden field payload');
  await expect(page.locator('.relation-grid-shell')).toHaveCount(0);
  await expect(page.locator('.relation-column-title')).toHaveCount(0);
  await expect(page.locator('.workbench-result-panel')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Refresh', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Insert row', exact: true })).toHaveCount(0);
  const readCount = evidence.reads.length;
  await page.getByTestId('workbench-table').getByRole('button', { name: 'DDL', exact: true }).click();
  await expect(page.locator('.relation-ddl')).toHaveCount(0);
  expect(evidence.reads.length).toBe(readCount);
  assertReadOnlyEvidence(evidence);
});

test('readonly real component harness retains browse and DDL while row writes stay disabled', async ({ page }) => {
  const evidence = await prepare(page);
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await mountReadonlyComponent(page);
  await assertIdentity(page, east, 'readonly');
  await expect(page.locator('.relation-grid')).toContainText('East visible');
  await expect(page.getByRole('button', { name: 'Insert row', exact: true })).toBeDisabled();
  await expect(page.locator('.relation-grid').getByRole('button', { name: 'Edit', exact: true })).toHaveCount(0);
  await expect(page.locator('.relation-grid').getByRole('button', { name: 'Delete', exact: true })).toHaveCount(0);
  await page.getByTestId('workbench-table').getByRole('button', { name: 'DDL', exact: true }).click();
  await expect(page.locator('.relation-ddl textarea')).toHaveValue(/CREATE TABLE "DeviceID:Main"/u);
  await page.getByTestId('workbench-table').getByRole('button', { name: '设计器', exact: true }).click();
  await expect(page.locator('.schema-designer')).toHaveCount(0);
  assertReadOnlyEvidence(evidence);
});

test('long full page remains bounded and the next short page stops pagination', async ({ page }) => {
  const longValue = `LongValue:${'x'.repeat(8_400)}`;
  const firstPage = Array.from({ length: 50 }, (_, index) => [index + 1, index === 0 ? longValue : `Asset ${index}`, null]);
  const evidence = await prepare(page, {
    sql: (route, request) => rows(route, request.parameters?.offset?.integerValue === 50 ? [[51, 'Final page row', null]] : firstPage),
  });
  await openTable(page);
  await assertIdentity(page, east, 'longContent');
  await expect(page.locator('.relation-pager')).toContainText('50 visible rows');
  await expect(page.locator('.relation-grid')).toContainText('NULL');
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await assertIdentity(page, east, 'normal');
  await expect(page.locator('.relation-grid')).toContainText('Final page row');
  await expect(page.locator('.relation-pager')).toContainText('Page 2');
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  expect(evidence.reads.at(-1)?.parameters?.offset?.integerValue).toBe(50);
  expect(evidence.reads.every((request) => (request.parameters?.limit?.integerValue ?? 0) <= 200)).toBe(true);
  assertReadOnlyEvidence(evidence);
});

test('long field on a short page is identified without inventing a next page', async ({ page }) => {
  const longValue = `FieldPrefix:${'y'.repeat(8_400)}:FieldTail`;
  const evidence = await prepare(page, { sql: (route) => rows(route, [[1, longValue, null]]) });
  await openTable(page);
  await assertIdentity(page, east, 'longContent');
  await expect(page.locator('.relation-grid')).toContainText('FieldPrefix:');
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await expect(page.locator('.relation-pager')).toContainText('1 visible rows');
  assertReadOnlyEvidence(evidence);
});

test('same-name table ignores a delayed East response after selecting West', async ({ page }) => {
  let delayEast = false;
  const delayed = boundedGate();
  let pending = 0;
  let completed = 0;
  const evidence = await prepare(page, {
    sql: async (route, request) => {
      if (request.database === east && delayEast) {
        pending += 1;
        await delayed.wait;
        // The actual component can abort the obsolete request. Fulfillment of
        // a cancelled route is allowed to fail; the UI must still retain West.
        try { await rows(route, [[99, 'Stale East response', null]]); }
        catch (error) { if (!/closed|cancel|intercept|abort/iu.test(String(error))) throw error; }
        finally { completed += 1; }
        return;
      }
      await rows(route, [[1, request.database === east ? 'East visible' : 'West visible', null]]);
    },
  });
  try {
    await openTable(page);
    await assertIdentity(page, east, 'normal');
    delayEast = true;
    await page.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await page.locator('.schema-item--database').filter({ hasText: west }).click();
    await assertIdentity(page, west, 'normal');
    await expect(page.locator('.relation-grid')).toContainText('West visible');
    delayed.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await assertIdentity(page, west, 'normal');
    await expect(page.locator('.relation-grid')).not.toContainText('Stale East response');
    await expect(page.locator('.relation-grid')).not.toContainText('East visible');
    expect(evidence.reads.some((request) => request.database === west)).toBe(true);
    assertReadOnlyEvidence(evidence);
  } finally { delayed.release(); }
});

test('staged insert and dangerous delete can be cancelled without sending a write', async ({ page }) => {
  const evidence = await prepare(page);
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await stageInsert(page);
  let approval = page.getByRole('dialog', { name: 'Relation table edit batch' });
  await expect(approval).toContainText(`${east}.${tableName}`);
  await expect(approval).toContainText('INSERT INTO "DeviceID:Main"');
  expect(evidence.writes).toEqual([]);
  await approval.locator('.write-approval__actions').getByRole('button', { name: '返回编辑', exact: true }).click();
  await expect(approval).toHaveCount(0);
  await page.locator('.relation-grid').getByRole('button', { name: 'Delete', exact: true }).first().click();
  approval = page.getByRole('dialog', { name: 'Relation table edit batch' });
  await expect(approval).toContainText('DELETE FROM "DeviceID:Main"');
  await expect(approval.getByRole('button', { name: '确认执行 1 项高风险操作', exact: true })).toBeDisabled();
  await page.keyboard.press('Escape');
  await expect(approval).toHaveCount(0);
  assertReadOnlyEvidence(evidence);
});

test('confirmed row batch uses the original database once and refreshes without replay', async ({ page }) => {
  const batchGate = boundedGate();
  const evidence = await prepare(page, {
    batch: async (route, request) => { await batchGate.wait; await batchResult(route, request.statements.length); },
  });
  try {
    await openTable(page);
    await assertIdentity(page, east, 'normal');
    await stageInsert(page);
    const approval = page.getByRole('dialog', { name: 'Relation table edit batch' });
    const confirm = approval.getByRole('button', { name: '确认执行 1 项操作', exact: true });
    // Two native activations in the same event turn exercise the production
    // busy guard, before Vue has painted the disabled button.
    await confirm.evaluate((button: HTMLButtonElement) => { button.click(); button.click(); });
    await expect.poll(() => evidence.writes.length, { timeout: 5_000 }).toBe(1);
    await expect(approval).toHaveCount(0);
    batchGate.release();
    await expect(approval).toHaveCount(0);
    await expect(page.locator('.relation-toolbar__meta')).toContainText('0 staged edits');
    expect(evidence.writes).toHaveLength(1);
    expect(evidence.writes[0].database).toBe(east);
    expect(evidence.writes[0].statements.map((statement) => statement.sql)).toEqual([
      'BEGIN', expect.stringContaining('INSERT INTO "DeviceID:Main"'), 'COMMIT',
    ]);
    expect(evidence.unexpected).toEqual([]);
    expect(evidence.reads.every((request) => /^SELECT\b/iu.test(request.sql))).toBe(true);
  } finally { batchGate.release(); }
});

test('opening existing DDL in SQL restores input without executing it', async ({ page }) => {
  const evidence = await prepare(page);
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await page.getByTestId('workbench-table').getByRole('button', { name: 'DDL', exact: true }).click();
  await expect(page.locator('.relation-ddl textarea')).toHaveValue(/CREATE TABLE "DeviceID:Main"/u);
  const readCount = evidence.reads.length;
  await page.getByRole('button', { name: 'Open in SQL', exact: true }).click();
  await expect(page.locator('.query-workspace')).toBeVisible();
  await expect(page.locator('.cm-content')).toContainText('CREATE TABLE "DeviceID:Main"');
  expect(evidence.reads.length).toBe(readCount);
  assertReadOnlyEvidence(evidence);
});

test('history restores its write command to SQL input without automatic replay', async ({ page }) => {
  const evidence = await prepare(page);
  await openTable(page);
  await assertIdentity(page, east, 'normal');
  await page.getByTestId('workbench-table').getByRole('button', { name: 'History', exact: true }).click();
  const history = page.locator('.workbench-history-entry').filter({ hasText: 'Saved row draft' });
  await expect(history).toContainText(historySql);
  const readCount = evidence.reads.length;
  await history.getByRole('button', { name: '恢复', exact: true }).click();
  await expect(page.locator('.query-workspace')).toBeVisible();
  await expect(page.locator('.cm-content')).toContainText(historySql);
  expect(evidence.reads.length).toBe(readCount);
  assertReadOnlyEvidence(evidence);
});

async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  const workbench = page.getByTestId('workbench-table');
  await expect(workbench).toHaveAttribute('data-page-state', state);
  await expect(workbench).toHaveAttribute('data-database', database);
  await expect(workbench).toHaveAttribute('data-resource-key', tableName);
  await expect(workbench).toHaveAttribute('data-legacy-key', `table:${tableName}`);
  await expect(page.locator('.relation-toolbar__title')).toHaveText(tableName);
}

async function openTable(page: Page): Promise<void> {
  const query = new URLSearchParams({ database: east, model: 'table', node: tableName, tool: 'table' });
  await page.goto(`/admin/app/sql?${query}`);
}

async function stageInsert(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Insert row', exact: true }).click();
  await page.locator('.relation-field').filter({ hasText: 'DeviceID' }).locator('input').fill('2');
  await page.locator('.relation-field').filter({ hasText: 'MixedCase:Name' }).locator('input').fill('New staged value');
  await page.getByRole('button', { name: 'Stage insert', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Relation table edit batch' })).toBeVisible();
}

function assertReadOnlyEvidence(evidence: Evidence): void {
  expect(evidence.unexpected).toEqual([]);
  expect(evidence.writes).toEqual([]);
  for (const request of evidence.reads) {
    expect(request.sql).toMatch(/^SELECT\b/iu);
    expect(request.sql).not.toContain('WorkbenchDraftMustNotRun');
    expect(request.sql).not.toContain('HistoryDraftMustNotRun');
    expect(request.sql).not.toMatch(/\b(?:INSERT|UPDATE|DELETE|CREATE|DROP|ALTER)\b/iu);
  }
}

function boundedGate(): { wait: Promise<void>; release: () => void; readonly released: boolean } {
  let resolve: () => void = () => {};
  let released = false;
  const wait = new Promise<void>((done) => { resolve = done; });
  let timer: ReturnType<typeof setTimeout> | undefined;
  function release(): void { if (!released) { released = true; clearTimeout(timer); resolve(); } }
  return {
    get wait() { if (!timer && !released) timer = setTimeout(() => release(), 8_000); return wait; },
    release,
    get released() { return released; },
  };
}

async function mountReadonlyComponent(page: Page): Promise<void> {
  // This explicit Vite-only component harness supplies the existing readOnly
  // prop. The routed host has no verified permission-capability adapter yet.
  const componentPath = '/src/components/RelationalTableWorkbench.vue';
  const componentSource = await (await page.request.get(componentPath, { timeout: 5_000 })).text();
  const authPath = '/src/stores/auth.ts';
  const authSource = await (await page.request.get(authPath, { timeout: 5_000 })).text();
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`Readonly harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, table }) => {
    const [vue, naive, piniaModule, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb17-readonly-harness'; host.style.height = '100vh'; document.body.append(host);
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, { targetDb: database, table, tables: [table], readOnly: true }) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
  }, { modules, componentPath, authPath, database: east, table });
}

async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  await page.addInitScript(({ database, draftSql, historySql, tableName }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    localStorage.setItem('sndb.sql.console.tabs.v1', JSON.stringify({ tabs: [{ id: 'wb17-draft', title: 'Unexecuted fixture draft', db: database, sql: draftSql, results: [], summary: '', errorMsg: '', ranOnce: false, source: 'manual', createdAt: 1, updatedAt: 1 }], activeTabId: 'wb17-draft' }));
    localStorage.setItem('sndb.workbench.history.v1', JSON.stringify({ entries: [{ id: 'wb17-history', kind: 'operation', status: 'success', title: 'Saved row draft', target: tableName, database, connectionId: 'managed-local', connectionName: 'Managed Local', model: 'table', action: 'delete', command: historySql, summary: 'Fixture history input only', createdAt: 1 }] }));
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
  }, { database: east, draftSql, historySql, tableName });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb17-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [table], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      if (suffix === '/sql') {
        const request = { database, ...(route.request().postDataJSON() as Omit<SqlRequest, 'database'>) };
        evidence.reads.push(request);
        if (options.sql) return options.sql(route, request);
        return rows(route, [[1, database === east ? 'East visible' : 'West visible', null]]);
      }
      if (suffix === '/sql/batch') {
        const request = { database, ...(route.request().postDataJSON() as Omit<Evidence['writes'][number], 'database'>) };
        evidence.writes.push(request);
        if (options.batch) return options.batch(route, request);
        return batchResult(route, request.statements.length);
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb17_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}

async function rows(route: Route, values: unknown[][]): Promise<void> {
  await route.fulfill({ status: 200, contentType: 'application/x-ndjson', body: [
    JSON.stringify({ type: 'meta', columns: columns.map((column) => column.name) }),
    ...values.map((value) => JSON.stringify(value)),
    JSON.stringify({ type: 'end', rowCount: values.length, recordsAffected: -1, elapsedMs: 1 }),
  ].join('\n') });
}

async function batchResult(route: Route, statementCount: number): Promise<void> {
  expect(statementCount).toBeLessThanOrEqual(10);
  await route.fulfill({ status: 200, contentType: 'application/x-ndjson', body: Array.from({ length: statementCount }, (_, index) => JSON.stringify({ type: 'end', rowCount: 0, recordsAffected: index === 1 ? 1 : 0, elapsedMs: 1 })).join('\n') });
}

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
