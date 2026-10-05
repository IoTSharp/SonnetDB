import { expect, test, type Page, type Route } from '@playwright/test';

// Routed Vue Workbench evidence with bounded API fixtures. These tests prove
// browser/component isolation only; they do not claim Server authorization,
// desktop host, VS Code, or release evidence.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const collectionName = 'DeviceProfiles:Main';
const indexName = 'Search:Main';
const draftQuery = 'draft must survive permission';
const secretPayload = 'FullText secret payload must be cleared';

const fullTextIndex = {
  collection: collectionName,
  name: indexName,
  fields: ['title', 'body'],
  tokenizer: 'unicode',
  documentCount: 2,
  termCount: 9,
};

interface SearchRequest {
  database: string;
  body: Record<string, unknown>;
}

interface FindRequest {
  database: string;
  body: Record<string, unknown>;
}

interface Evidence {
  searches: SearchRequest[];
  finds: FindRequest[];
  writes: Array<{ database: string; path: string; body: unknown }>;
  unexpected: string[];
}

interface FixtureOptions {
  search?: (route: Route, request: SearchRequest) => Promise<void>;
  find?: (route: Route, request: FindRequest) => Promise<void>;
  analyze?: (route: Route, request: SearchRequest) => Promise<void>;
}

test.setTimeout(30_000);

test('same collection and fulltext index keep database identity when switching East to West', async ({ page }) => {
  const evidence = await prepare(page);
  await openFullText(page, east);
  await assertIdentity(page, east, 'normal');
  await expect(surface(page)).toContainText(`${collectionName}.${indexName}`);

  await search(page, 'East query');
  await expect(surface(page)).toContainText('East fulltext document');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await assertIdentity(page, west, 'normal');
  await expect(surface(page)).toContainText(`${collectionName}.${indexName}`);
  await search(page, 'West query');
  await expect(surface(page)).toContainText('West fulltext document');

  expect(evidence.searches.map((request) => request.database)).toContain(east);
  expect(evidence.searches.map((request) => request.database)).toContain(west);
  expect(evidence.searches.every((request) => request.body.collection === collectionName)).toBe(true);
  expect(evidence.searches.every((request) => request.body.index === indexName)).toBe(true);
  assertFixtureEvidence(evidence);
});

for (const status of [401, 403]) {
test(`HTTP ${status} search denial clears hits and result payload while retaining the query draft`, async ({ page }) => {
  let deny = false;
  const evidence = await prepare(page, {
    search: (route, request) => deny
      ? json(route, { code: 'permission_denied', message: secretPayload }, status)
      : searchResult(route, request.database),
  });
  await openFullText(page, east);
  await search(page, secretPayload);
  await expect(surface(page)).toContainText('East fulltext document');

  deny = true;
  await surface(page).locator('.fulltext-query-editor textarea').fill(draftQuery);
  await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
  await assertState(page, 'permission');
  // The table shell may remain mounted while permission state is locked; the
  // rendered rows and inspector payload must be gone.
  await expect(surface(page).locator('.fulltext-grid .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-document')).toHaveCount(0);
  // The shared result drawer remains mounted (and hidden/shown by the host),
  // while its result payload and alerts are cleared by the permission lock.
  await expect(page.locator('.workbench-result-panel__result')).toHaveCount(0);
  await expect(page.locator('.workbench-result-panel__alert')).toHaveCount(0);
  await expect(surface(page)).not.toContainText('East fulltext document');
  await expect(surface(page)).not.toContainText(secretPayload);
  await expect(surface(page).locator('.fulltext-query-editor textarea')).toHaveValue(draftQuery);
  expect(evidence.searches.at(-1)?.database).toBe(east);
  assertFixtureEvidence(evidence);
});
}

test('a delayed search is isolated across database switch and unmount', async ({ page }) => {
  let delayEast = false;
  let pending = 0;
  let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, {
    search: async (route, request) => {
      if (delayEast && request.database === east) {
        pending += 1;
        await gate.wait;
        try { await searchResult(route, east, 'Stale East payload must not render'); }
        catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
        finally { completed += 1; }
        return;
      }
      await searchResult(route, request.database);
    },
  });
  try {
    await openFullText(page, east);
    delayEast = true;
    await surface(page).locator('.fulltext-query-editor textarea').fill('delayed east');
    await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);

    await page.locator('.schema-item--database').filter({ hasText: west }).click();
    await assertIdentity(page, west, 'normal');
    await expect(surface(page)).not.toContainText('Stale East payload must not render');
    await search(page, 'west current');
    await expect(surface(page)).toContainText('West fulltext document');

    await page.goto(`/admin/app/sql?tool=table&database=${encodeURIComponent(west)}&model=table&node=MissingTable`);
    await expect(surface(page)).toHaveCount(0);
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(page.locator('body')).not.toContainText('Stale East payload must not render');
    const history = await historyEntries(page);
    expect(JSON.stringify(history)).not.toContain('Stale East payload must not render');
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('defensive preview caps an oversized response at the requested topK before loading and export', async ({ page }) => {
  const sentinel = 'FullText sentinel must not export';
  const evidence = await prepare(page, {
    search: (route, request) => {
      const hits = Array.from({ length: 101 }, (_, index) => ({
        documentId: index === 100 ? sentinel : `fulltext-doc-${index}`,
        score: 1 - index / 10_000,
      }));
      return json(route, { hits });
    },
  });
  await openFullText(page, east);
  await surface(page).locator('.fulltext-topk, .fulltext-toolbar__topk').locator('input').fill('100');
  await search(page, 'bounded query');

  const rendered = await surface(page).locator('.fulltext-doc-button').count();
  expect(rendered).toBeLessThanOrEqual(100);
  await expect(surface(page)).not.toContainText(sentinel);
  const budgetMarker = surface(page).locator('[data-testid="fulltext-result-budget"]');
  if (await budgetMarker.count()) {
    await expect(budgetMarker).toContainText('100');
    await expect(budgetMarker).toContainText(/truncated|截断/u);
  } else {
    await expect(surface(page)).toContainText(/truncated|截断/u);
  }

  const exported = await exportResultJson(page);
  expect(exported).toHaveLength(100);
  expect(JSON.stringify(exported)).not.toContain(sentinel);
  expect(evidence.searches.at(-1)?.body.topK).toBe(100);
  expect(evidence.finds.at(-1)?.body.limit).toBe(100);
  expect(evidence.finds.at(-1)?.body.ids).toHaveLength(100);
  expect(JSON.stringify(evidence.finds)).not.toContain(sentinel);
  expect((await historyEntries(page)).find((entry) => entry.action === 'search')).toMatchObject({ rowCount: 100 });
  assertFixtureEvidence(evidence);
});

test('Document Find 403 clears the search hit and document payload and retains the query draft', async ({ page }) => {
  let denyFind = false;
  const evidence = await prepare(page, {
    find: (route, request) => denyFind
      ? json(route, { code: 'permission_denied', message: secretPayload }, 403)
      : findResult(route, request.database),
  });
  await openFullText(page, east);
  await search(page, 'document load first');
  await expect(surface(page)).toContainText('East fulltext document');
  denyFind = true;
  await search(page, draftQuery);
  await assertState(page, 'permission');
  await expect(surface(page).locator('.fulltext-grid .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-document')).toHaveCount(0);
  await expect(page.locator('.workbench-result-panel__result')).toHaveCount(0);
  await expect(surface(page)).not.toContainText('East fulltext document');
  await expect(surface(page)).not.toContainText(secretPayload);
  await expect(surface(page).locator('.fulltext-query-editor textarea')).toHaveValue(draftQuery);
  expect(evidence.finds).toHaveLength(2);
  assertFixtureEvidence(evidence);
});

test('Analyzer 403 removes prior tokens and result payload while keeping the search query draft', async ({ page }) => {
  let denyAnalyze = false;
  const evidence = await prepare(page, {
    analyze: (route) => denyAnalyze
      ? json(route, { code: 'permission_denied', message: secretPayload }, 403)
      : json(route, { tokens: [{ text: 'PriorAnalyzerTokenPayload', startOffset: 0, endOffset: 25, positionIncrement: 1 }] }),
  });
  await openFullText(page, east);
  await surface(page).locator('.fulltext-query-editor textarea').fill(draftQuery);
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: 'Analyzer', exact: true }).click();
  await surface(page).getByRole('button', { name: 'Analyze', exact: true }).click();
  await expect(surface(page).locator('.fulltext-token-list')).toContainText('PriorAnalyzerTokenPayload');
  denyAnalyze = true;
  await surface(page).getByRole('button', { name: 'Analyze', exact: true }).click();
  await assertState(page, 'permission');
  await expect(surface(page)).not.toContainText('PriorAnalyzerTokenPayload');
  await expect(surface(page)).not.toContainText(secretPayload);
  await expect(page.locator('.workbench-result-panel__result')).toHaveCount(0);
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: '全文检索', exact: true }).click();
  await expect(surface(page).locator('.fulltext-query-editor textarea')).toHaveValue(draftQuery);
  assertFixtureEvidence(evidence);
});

test('rebuild and import approvals are invalidated when their database context changes', async ({ page }) => {
  const evidence = await prepare(page);
  await openFullText(page, east);
  await surface(page).getByRole('button', { name: 'Rebuild', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText(indexName);
  // The approval backdrop intentionally captures pointer input. Navigating to
  // the same Workbench in another database exercises the host unmount/context
  // boundary and must discard the staged approval without sending a write.
  await openFullText(page, west);
  await assertIdentity(page, west, 'normal');
  await expect(page.getByRole('dialog')).toHaveCount(0);

  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: '数据导入', exact: true }).click();
  await surface(page).locator('.fulltext-import-panel textarea').fill('{"_id":"import-1","title":"Approval context"}');
  await surface(page).getByRole('button', { name: '解析并暂存', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('FullText document import');
  await openFullText(page, east);
  await assertIdentity(page, east, 'normal');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(evidence.writes).toEqual([]);
  assertFixtureEvidence(evidence);
});

function surface(page: Page) { return page.getByTestId('workbench-fulltext'); }

async function openFullText(page: Page, database: string): Promise<void> {
  await page.goto(`/admin/app/sql?tool=fulltext&database=${encodeURIComponent(database)}&model=fulltext&node=${encodeURIComponent(`${collectionName}.${indexName}`)}`);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toContainText(indexName);
}

async function search(page: Page, query: string): Promise<void> {
  const editor = surface(page).locator('.fulltext-query-editor textarea');
  await editor.fill(query);
  await surface(page).getByRole('button', { name: 'Search', exact: true }).click();
  await expect(surface(page).locator('.fulltext-grid')).toBeVisible();
}

async function assertState(page: Page, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
}

async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  const target = surface(page);
  await expect(target).toHaveAttribute('data-database', database);
  await expect(target).toContainText(`${collectionName}.${indexName}`);
  await assertState(page, state);
}

function boundedGate() {
  let release!: () => void;
  let released = false;
  const timer = setTimeout(() => release(), 10_000);
  const wait = new Promise<void>((resolve) => {
    release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } };
  });
  return { wait, release };
}

async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { searches: [], finds: [], writes: [], unexpected: [] };
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const exportWindow = window as Window & { __wb19Exports?: string[] };
    exportWindow.__wb19Exports = [];
    const originalCreateObjectUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => {
      void value.text().then((content) => { exportWindow.__wb19Exports?.push(content); });
      return originalCreateObjectUrl(value);
    }) as typeof URL.createObjectURL;
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {} removeEventListener(): void {}
      dispatchEvent(): boolean { return true; }
      close(): void { /* fixture */ }
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database: east });

  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb19-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (!matched || ![east, west].includes(matched[1])) {
      evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
      return json(route, { code: 'wb19_contract_not_mocked', message: path }, 501);
    }
    const database = matched[1];
    const suffix = matched[2];
    if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
    if (suffix === '/fulltext/indexes') return json(route, { indexes: [fullTextIndex] });
    if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
    if (suffix === '/vector/indexes') return json(route, { indexes: [] });
    if (suffix === '/mq/topics') return json(route, { topics: [] });
    if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
    if (suffix === '/fulltext/search-preview') {
      const request: SearchRequest = { database, body: route.request().postDataJSON() as Record<string, unknown> };
      evidence.searches.push(request);
      if (options.search) return options.search(route, request);
      return searchResult(route, database);
    }
    if (suffix === '/fulltext/analyze') {
      if (options.analyze) return options.analyze(route, { database, body: route.request().postDataJSON() as Record<string, unknown> });
      return json(route, { tokens: [{ text: 'fixture', startOffset: 0, endOffset: 7, positionIncrement: 1 }] });
    }
    const findPath = `/documents/${collectionName}/find`;
    if (suffix === findPath) {
      const request: FindRequest = { database, body: route.request().postDataJSON() as Record<string, unknown> };
      evidence.finds.push(request);
      if (options.find) return options.find(route, request);
      return findResult(route, database);
    }
    if (suffix === '/maintenance') {
      evidence.writes.push({ database, path: suffix, body: route.request().postDataJSON() });
      // Match MaintenanceEndpointHandler.RebuildIndexResponse's synchronous
      // document FullText terminal shape. Read/staging-only cases still fail
      // their unchanged evidence assertion if this endpoint is called.
      return json(route, {
        operation: 'rebuild_index',
        status: 'ok',
        success: true,
        message: '索引维护操作完成。',
        completedUtc: '2026-10-06T00:00:00.000Z',
        checks: [{ name: 'index', status: 'ok', message: `document/${collectionName}/${indexName}` }],
        index: {
          model: 'document',
          owner: collectionName,
          name: indexName,
          kind: 'fulltext',
          mode: 'sync_touch',
          planned: false,
          rebuildable: true,
          documentCount: fullTextIndex.documentCount,
        },
      });
    }
    if (suffix.includes(`/documents/${collectionName}/`)) {
      evidence.writes.push({ database, path: suffix, body: route.request().postDataJSON() });
      return json(route, { code: 'wb19_fixture_write_forbidden', message: 'fixture writes must be gated' }, 501);
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb19_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}

async function searchResult(route: Route, database: string, documentId = ''): Promise<void> {
  const id = documentId || `${database === east ? 'east' : 'west'}-fulltext-document`;
  await json(route, { hits: [{ documentId: id, score: 0.987 }] });
}

async function findResult(route: Route, database: string): Promise<void> {
  const request = route.request().postDataJSON() as { ids?: string[]; limit?: number };
  const limit = Math.min(100, Math.max(0, Math.floor(request.limit ?? 100)));
  const ids = (request.ids ?? [`${database === east ? 'east' : 'west'}-fulltext-document`]).slice(0, limit);
  const documents = ids.map((id) => ({ id, document: { title: `${database === east ? 'East' : 'West'} fulltext document`, body: 'Fixture body' }, version: 1 }));
  await json(route, { collection: collectionName, documents, count: documents.length, limit: request.limit ?? documents.length, skip: 0, continuationToken: null, hasMore: false });
}

function assertFixtureEvidence(evidence: Evidence): void {
  expect(evidence.unexpected).toEqual([]);
  expect(evidence.writes).toEqual([]);
}

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function historyEntries(page: Page): Promise<Array<Record<string, unknown>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries);
}

async function exportResultJson(page: Page): Promise<unknown[]> {
  const before = await page.evaluate(() => (window as Window & { __wb19Exports?: string[] }).__wb19Exports?.length ?? 0);
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } })));
  const exportButton = page.locator('.workbench-result-panel').getByTitle('Export result set as JSON', { exact: true });
  await expect(exportButton).toBeVisible();
  await exportButton.click();
  await expect.poll(() => page.evaluate((offset) => (window as Window & { __wb19Exports?: string[] }).__wb19Exports?.length ?? 0, before), { timeout: 5_000 }).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as Window & { __wb19Exports?: string[] }).__wb19Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('FullText preview export exceeded the fixture read budget.');
  return JSON.parse(content) as unknown[];
}
