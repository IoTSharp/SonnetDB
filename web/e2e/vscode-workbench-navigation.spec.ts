import { expect, test, type Page, type Route } from '@playwright/test';
import { createRequire } from 'node:module';
import type { SonnetDbConnectionProfile } from '../../extensions/sonnetdb-vscode/src/core/types';
import type { TreeNode } from '../../extensions/sonnetdb-vscode/src/tree/sonnetdbTreeDataProvider';

// The extension package is CommonJS; use Playwright's TypeScript require hook
// so these are the actual source exports, without copying or rebuilding them.
const { buildWorkbenchUrl, workbenchTargetFromNode } = createRequire(import.meta.url)(
  '../../extensions/sonnetdb-vscode/src/core/workbenchResource.ts',
) as typeof import('../../extensions/sonnetdb-vscode/src/core/workbenchResource');

// This suite uses the actual extension link producer and the actual Web router.
// Authentication and API responses are browser fixtures, not Server permission evidence.
const basePath = `/${(process.env.SONNETDB_WEB_BASE_PATH ?? '/').split('/').filter(Boolean).join('/')}`.replace(/\/?$/u, '/');
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const defaultDatabase = 'DefaultDB:Other';
const name = 'DeviceID:Main';
const decoy = 'Decoy:First';
const draftSql = 'SELECT * FROM WorkbenchDraftMustNotRun;';
const createdUtc = '2026-10-06T00:00:00Z';
const profile: SonnetDbConnectionProfile = {
  id: 'wb16-fixture', label: 'Workbench fixture', kind: 'remote',
  baseUrl: 'https://unused.invalid', defaultDatabase, tokenSecretKey: 'must-not-forward-secret-key',
};
const measurement = (resourceName: string) => ({ name: resourceName, columns: [
  { name: 'time', role: 'time', dataType: 'TIMESTAMP' },
  { name: 'Value', role: 'field', dataType: 'DOUBLE' },
] });
const table = (resourceName: string) => ({ name: resourceName, columns: [
  { name: 'DeviceID', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 },
], primaryKey: ['DeviceID'], indexes: [], createdUtc });
const collection = (resourceName: string) => ({ name: resourceName, jsonIndexes: [], fullTextIndexes: [], createdUtc });
const vector = (resourceName: string) => ({ measurement: resourceName, column: 'Embedding:V2', kind: 'flat', dimension: 3, metric: 'cosine', params: [], rowCount: 0 });
const fulltext = (resourceName: string) => ({ collection: resourceName, name: 'Text:V2', fields: ['$.name'], tokenizer: 'unicode', documentCount: 0, termCount: 0 });
const topic = (resourceName: string) => ({ topic: resourceName, messageCount: 0, nextOffset: 0 });
const bucket = (resourceName: string) => ({ name: resourceName, purpose: '', createdUtc, updatedUtc: createdUtc, objectCount: 0, totalBytes: 0 });
const graph = (resourceName: string) => ({ name: resourceName, storageId: '36600000-0000-0000-0000-000000000001', recordFormatVersion: 1 });
const index = { id: `table:${name}:IX:Device`, model: 'table', owner: name, name: 'IX:Device', kind: 'btree', state: 'ready', includedInBackup: true, rebuildable: true, columns: [] };
const backupStatus = { backupCapable: true, hasRestoreManifest: false, segmentCount: 0, walFileCount: 1, totalBytes: 0, memTablePointCount: 0, checkpointLsn: 0, nextSegmentId: 1 };
const base = { profile, database: east };
const targets: Array<{ node: TreeNode; model: string; selectedName: string; workspace: string }> = [
  { node: { ...base, kind: 'measurement', measurement: measurement(name) }, model: 'measurement', selectedName: name, workspace: '.measurement-workbench' },
  { node: { ...base, kind: 'table', table: table(name) }, model: 'table', selectedName: name, workspace: '.relation-workbench' },
  { node: { ...base, kind: 'document', collection: collection(name) }, model: 'document', selectedName: name, workspace: '.document-workbench' },
  { node: { ...base, kind: 'kvKeyspace', keyspace: name }, model: 'kv', selectedName: name, workspace: '.kv-workbench' },
  { node: { ...base, kind: 'vectorIndex', index: vector(name) }, model: 'vector', selectedName: `${name}.Embedding:V2`, workspace: '.vector-workbench' },
  { node: { ...base, kind: 'fullTextIndex', index: fulltext(name) }, model: 'fulltext', selectedName: `${name}.Text:V2`, workspace: '.fulltext-workbench' },
  { node: { ...base, kind: 'mqTopic', topic: topic(name) }, model: 'mq', selectedName: name, workspace: '.mq-workbench' },
  { node: { ...base, kind: 'objectBucket', bucket: bucket(name) }, model: 'bucket', selectedName: name, workspace: '.object-workbench' },
  { node: { ...base, kind: 'graph', graph: graph(name) }, model: 'graph', selectedName: name, workspace: '.graph-workbench' },
  { node: { ...base, kind: 'index', index }, model: 'index', selectedName: index.name, workspace: '.query-workspace' },
  { node: { ...base, kind: 'backup', backupStatus }, model: 'backup', selectedName: 'Backup status', workspace: '.query-workspace' },
];

interface Evidence {
  sql: Array<{ database: string; sql: string }>;
  apiPaths: string[];
  unexpected: string[];
  loginCalls: number;
}

test.setTimeout(30_000);

// Eleven targets plus six navigation/authentication cases; one worker and zero
// fixture retries keep each root or proxy run within ten minutes.
for (const target of targets) {
  test(`extension ${target.model} link selects the original resource in Web`, async ({ page, baseURL }) => {
    const evidence = await prepare(page);
    const url = link(baseURL!, target.node);
    assertNavigationFields(url, target.node);
    await page.goto(url);
    await assertSelection(page, east, target.model, target.selectedName, target.workspace);
    if (target.model === 'index') {
      expect(new URL(url).searchParams.get('node')).toBe(index.id);
      // Both owners have the same index display name. Their existing metadata
      // differs, so this also proves the complete owner key selected the target.
      await expect(page.locator('.schema-item--index.is-active')).toHaveAttribute('title', 'btree · ready · rebuildable');
    }
    if (target.model === 'graph') {
      expect(workbenchTargetFromNode(target.node)?.resource?.beta).toBe(true);
      expect(workbenchTargetFromNode(target.node)?.resource?.stability).toBe('beta');
      await expect(page.locator('.graph-head__title-row strong')).toHaveText(name);
    }
    if (!['index', 'backup'].includes(target.model)) {
      await expect(page.locator('.workspace-tab[aria-selected="true"]')).toContainText(target.selectedName);
    }
    await assertReadOnlyNavigation(page, evidence, ['measurement', 'table'].includes(target.model));
  });
}

test('same-name MQ links retain East and West database identity', async ({ page, baseURL }) => {
  const evidence = await prepare(page);
  const first = targets[6].node as Extract<TreeNode, { kind: 'mqTopic' }>;
  await page.goto(link(baseURL!, first));
  await assertSelection(page, east, 'mq', name, '.mq-workbench');
  await expect(page.locator('.mq-toolbar__meta')).toContainText(east);
  await page.goto(link(baseURL!, { ...first, database: west }));
  await assertSelection(page, west, 'mq', name, '.mq-workbench');
  await expect(page.locator('.mq-toolbar__meta')).toContainText(west);
  expect(evidence.apiPaths.some((path) => decodeURIComponent(path).includes(`/db/${east}/mq/`))).toBe(true);
  expect(evidence.apiPaths.some((path) => decodeURIComponent(path).includes(`/db/${west}/mq/`))).toBe(true);
  await assertReadOnlyNavigation(page, evidence, false);
});

test('database-only node beats a different connection default without running the draft', async ({ page, baseURL }) => {
  const evidence = await prepare(page);
  const node: TreeNode = { kind: 'database', profile, name: east, active: false };
  const url = link(baseURL!, node);
  assertNavigationFields(url, node);
  await page.goto(url);
  await expect(page.locator('.schema-item--database.is-active strong')).toHaveText(east);
  await expect(page.locator('.schema-item--measurement.is-active strong')).toHaveText(decoy);
  await expect(page.locator('.query-workspace')).toBeVisible();
  await assertReadOnlyNavigation(page, evidence, false);
});

for (const missing of [false, true]) {
  test(`${missing ? 'missing' : 'unknown'} database/node falls back safely without execution`, async ({ page, baseURL }) => {
    const evidence = await prepare(page);
    const url = new URL(link(baseURL!, targets[8].node));
    if (missing) {
      url.searchParams.delete('database');
      url.searchParams.delete('node');
    } else {
      url.searchParams.set('database', 'UnknownDB:NeverExists');
      url.searchParams.set('node', 'UnknownGraph:NeverExists');
    }
    await page.goto(url.toString());
    await expect(page.locator('.schema-item--database.is-active strong')).toHaveText(defaultDatabase);
    await expect(page.locator('.schema-item--measurement.is-active strong')).toHaveText(decoy);
    await expect(page.locator('.graph-workbench')).toBeVisible();
    await expect(page.locator('.schema-item--object.is-active')).toHaveCount(1);
    await assertReadOnlyNavigation(page, evidence, false);
  });
}

test('anonymous extension link survives successful fixture login with its original target', async ({ page, baseURL }) => {
  const evidence = await prepare(page, false);
  const destination = new URL(link(baseURL!, targets[6].node));
  await page.goto(destination.toString());
  await expect(page).toHaveURL((url) => url.pathname === `${basePath}admin/login`);
  expect(new URL(page.url()).searchParams.get('redirect')).toBe(destination.pathname.slice(basePath.length - 1) + destination.search);
  await page.getByPlaceholder('admin', { exact: true }).fill('fixture-user');
  await page.getByPlaceholder('输入管理员密码').fill('fixture-password');
  await page.getByRole('button', { name: '登录后台', exact: true }).click();
  await expect(page).toHaveURL((url) => sameNavigationUrl(url, destination));
  await assertSelection(page, east, 'mq', name, '.mq-workbench');
  expect(evidence.loginCalls).toBe(1);
  await assertReadOnlyNavigation(page, evidence, false);
});

test('authenticated login redirect preserves the resource without another login or draft execution', async ({ page, baseURL }) => {
  const evidence = await prepare(page);
  const destination = new URL(link(baseURL!, targets[7].node));
  const url = new URL(`${basePath}admin/login`, baseURL);
  url.searchParams.set('redirect', destination.pathname.slice(basePath.length - 1) + destination.search);
  await page.goto(url.toString());
  await expect(page).toHaveURL((url) => sameNavigationUrl(url, destination));
  await assertSelection(page, east, 'bucket', name, '.object-workbench');
  expect(evidence.loginCalls).toBe(0);
  await assertReadOnlyNavigation(page, evidence, false);
});

function link(baseURL: string, node: TreeNode): string {
  const target = workbenchTargetFromNode(node);
  if (!target) throw new Error('Expected a supported extension resource node.');
  return buildWorkbenchUrl(new URL(`${basePath}?token=must-not-forward-url-token&sql=must-not-forward-url-sql#secret`, baseURL).toString(), target);
}

function sameNavigationUrl(actual: URL, expected: URL): boolean {
  // Vue Router may serialize a colon literally while URLSearchParams encodes
  // it. Compare every decoded field without changing the navigation contract.
  return actual.origin === expected.origin && actual.pathname === expected.pathname
    && actual.hash === expected.hash && actual.searchParams.size === expected.searchParams.size
    && [...expected.searchParams.entries()].every(([key, value]) =>
      actual.searchParams.getAll(key).length === 1 && actual.searchParams.get(key) === value);
}

function assertNavigationFields(value: string, node: TreeNode): void {
  const url = new URL(value);
  const target = workbenchTargetFromNode(node)!;
  expect(url.pathname).toBe(`${basePath}admin/app/sql`);
  expect(url.searchParams.get('database')).toBe(target.database);
  expect(url.hash).toBe('');
  expect([...url.searchParams.keys()].sort()).toEqual(target.resource
    ? (['index', 'backup'].includes(target.resource.model) ? ['database', 'model', 'node'] : ['database', 'model', 'node', 'tool'])
    : ['database']);
  expect(value).not.toMatch(/must-not-forward|fixture-token|WorkbenchDraftMustNotRun/u);
}

async function assertSelection(page: Page, database: string, model: string, selectedName: string, workspace: string): Promise<void> {
  await expect(page.locator('.schema-item--database.is-active strong')).toHaveText(database);
  await expect(page.locator(`.schema-item--${model}.is-active strong`)).toHaveText(selectedName);
  await expect(page.locator('.schema-item--object.is-active')).toHaveCount(1);
  await expect(page.locator(workspace)).toBeVisible();
}

async function assertReadOnlyNavigation(page: Page, evidence: Evidence, allowModelPreview: boolean): Promise<void> {
  // Settle pending fixture responses after selection; this is not an action that
  // executes the SQL draft. Measurement/Relation retain their existing previews.
  await page.waitForTimeout(100);
  expect(evidence.unexpected).toEqual([]);
  expect(evidence.apiPaths.length).toBeGreaterThan(0);
  for (const path of evidence.apiPaths) expect(path.startsWith(basePath)).toBe(true);
  // Capture the real EventSource constructor input while avoiding SSE network
  // traffic. Global AppShell currently subscribes to all channels by omission.
  const subscriptions = await page.evaluate(() =>
    (window as unknown as { __wb16EventSources: Array<{ url: string; closed: boolean }> }).__wb16EventSources);
  expect(subscriptions.length).toBeGreaterThan(0);
  for (const subscription of subscriptions) {
    const url = new URL(subscription.url);
    expect(url.pathname).toBe(`${basePath}v1/events`);
    expect(url.searchParams.get('access_token')).toBe('fixture-token');
    expect(url.searchParams.get('stream')).toBeNull();
    expect([...url.searchParams.keys()]).toEqual(['access_token']);
  }
  if (!allowModelPreview) expect(evidence.sql).toEqual([]);
  for (const request of evidence.sql) {
    expect(request.sql).toMatch(/^SELECT\b/iu);
    expect(request.sql).toMatch(/LIMIT @limit/iu);
    expect(request.sql).not.toContain('WorkbenchDraftMustNotRun');
    expect(request.sql).not.toMatch(/\b(?:INSERT|UPDATE|DELETE|CREATE|DROP|ALTER)\b/iu);
    expect([east, defaultDatabase]).toContain(request.database);
  }
}

async function prepare(page: Page, authenticated = true): Promise<Evidence> {
  const evidence: Evidence = { sql: [], apiPaths: [], unexpected: [], loginCalls: 0 };
  await page.addInitScript(({ authenticated, defaultDatabase, draftSql }) => {
    localStorage.clear();
    if (authenticated) localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({
      profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }],
      activeProfileId: 'managed-local', activeDatabase: defaultDatabase,
    }));
    localStorage.setItem('sndb.sql.console.tabs.v1', JSON.stringify({ tabs: [{ id: 'wb16-draft', title: 'Unexecuted fixture draft', db: defaultDatabase, sql: draftSql, results: [], summary: '', errorMsg: '', ranOnce: false, source: 'manual', createdAt: 1, updatedAt: 1 }], activeTabId: 'wb16-draft' }));
    const subscriptions: Array<{ url: string; closed: boolean }> = [];
    Object.defineProperty(window, '__wb16EventSources', { configurable: true, value: subscriptions });
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly CONNECTING = 0; readonly OPEN = 1; readonly CLOSED = 2;
      readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      private readonly subscription: { url: string; closed: boolean };
      constructor(url: string | URL) {
        this.url = new URL(url, window.location.href).toString();
        this.subscription = { url: this.url, closed: false };
        subscriptions.push(this.subscription);
      }
      addEventListener(): void {} removeEventListener(): void {}
      dispatchEvent(): boolean { return true; }
      close(): void { this.readyState = 2; this.subscription.closed = true; }
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { authenticated, defaultDatabase, draftSql });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (!url.pathname.includes('/v1/') && !url.pathname.includes('/healthz')) return route.continue();
    evidence.apiPaths.push(url.pathname);
    if (!url.pathname.startsWith(basePath)) {
      evidence.unexpected.push(`API escaped deployment base: ${url.pathname}`);
      return json(route, { message: 'Wrong deployment base' }, 501);
    }
    const path = decodeURIComponent(url.pathname.slice(basePath.length - 1));
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb16-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 3 });
    if (path === '/v1/auth/login') { evidence.loginCalls += 1; return json(route, { username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }); }
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 3, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [defaultDatabase, east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [defaultDatabase, east, west].includes(matched[1])) {
      const db = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [measurement(decoy), measurement(name)], tables: [table(decoy), table(name)], documentCollections: [collection(decoy), collection(name)], indexes: [{ ...index, id: `table:${decoy}:IX:Device`, owner: decoy, kind: 'hash', state: 'failed' }, index], backupStatus });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [decoy, name] });
      if (suffix === '/vector/indexes') return json(route, { indexes: [vector(decoy), vector(name)] });
      if (suffix === '/fulltext/indexes') return json(route, { indexes: [fulltext(decoy), fulltext(name)] });
      if (suffix === '/mq/topics') return json(route, { topics: [topic(decoy), topic(name)] });
      if (suffix === '/s3') return json(route, [bucket(decoy), bucket(name)]);
      if (suffix === '/graphs') return json(route, [graph(decoy), graph(name)]);
      if (suffix === '/sql') {
        evidence.sql.push({ database: db, sql: (route.request().postDataJSON() as { sql: string }).sql });
        return route.fulfill({ status: 200, contentType: 'application/x-ndjson', body: [JSON.stringify({ type: 'meta', columns: ['time', 'Value'] }), JSON.stringify({ type: 'end', rowCount: 0, recordsAffected: -1, elapsedMs: 1 })].join('\n') });
      }
      if (/^\/documents\/[^/]+\/find$/u.test(suffix)) return json(route, { collection: name, documents: [], count: 0, limit: 100, skip: 0, continuationToken: null, hasMore: false });
      if (/^\/documents\/[^/]+\/count$/u.test(suffix)) return json(route, { collection: name, count: 0 });
      if (/^\/kv\/[^/]+\/stats$/u.test(suffix)) return json(route, { totalKeys: 0, activeKeys: 0, expiredKeys: 0, expiringKeys: 0, nearestExpiresAtUtc: null });
      if (/^\/kv\/[^/]+\/scan$/u.test(suffix)) return json(route, { entries: [], nextCursor: null, hasMore: false });
      if (/^\/mq\/[^/]+\/stats$/u.test(suffix)) return json(route, { topic: name, messageCount: 0, nextOffset: 0, consumerOffsets: {} });
      if (/^\/mq\/[^/]+\/offsets$/u.test(suffix)) return json(route, { topic: name, nextOffset: 0, consumers: [] });
      if (/^\/mq\/[^/]+\/retention$/u.test(suffix)) return json(route, { topic: name, retainedStartOffset: 0, retainedEndOffset: 0, retainedMessages: 0 });
      if (/^\/mq\/[^/]+\/browse$/u.test(suffix)) return json(route, { messages: [] });
      if (/^\/s3\/[^/]+$/u.test(suffix)) {
        if (url.searchParams.has('stats')) return json(route, { bucket: name, currentObjectCount: 0, currentSizeBytes: 0, objectVersionCount: 0, multipartUploadCount: 0 });
        if (url.searchParams.has('semantic')) return json(route, { bucket: name, asyncIngestionEnabled: false, thumbnailEnabled: false });
        return json(route, { bucket: name, prefix: '', objects: [], maxKeys: 100, isTruncated: false, continuationToken: null, nextContinuationToken: null });
      }
      if (/^\/graphs\/[^/]+\/operations\/overview$/u.test(suffix)) return json(route, { graph: graph(name), snapshotSequence: 1, vertexCount: 0, edgeCount: 0, labels: [], indexes: [], degreeHistogram: [], slowTraversals: [], slowTraversalSource: 'server_sql_diagnostics', capabilities: {} });
      if (/^\/graphs\/[^/]+\/operations\/visualization$/u.test(suffix)) return json(route, { snapshotSequence: 1, truncated: false, vertices: [], edges: [] });
      if (/^\/graphs\/[^/]+\/maintenance\/audit$/u.test(suffix)) return json(route, { items: [] });
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb16_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
