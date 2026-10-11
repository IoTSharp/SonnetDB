import { randomBytes } from 'node:crypto';
import { readFile, stat } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Real routed Web UI, Vite proxy, Kestrel authorization and Document APIs.
// API setup/login installs an actual session; this is not login UI, readonly
// host-prop, Studio, VS Code, installation, release or Server scan-budget evidence.
const database = 'wb26';
const collection = 'DeviceProfiles_Original';
const username = 'wb26_reader';
const password = 'Workbench26:OnlyLocal!';
const seedCount = 1_001;
const deniedId = 'Rejected:WriteDraft';
const deniedPayload = 'Rejected:DraftPayload';
const firstPayload = 'WB26:Payload:0000';
const documentPath = `/v1/db/${database}/documents/${collection}`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let administrator: AuthIdentity;
let reader: AuthIdentity;

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface FindResponse {
  collection: string;
  documents: Array<{ id: string; document: Record<string, unknown>; version: number }>;
  count: number;
  limit: number;
  skip: number;
  hasMore: boolean;
  continuationToken?: string | null;
}
interface HistoryEntry { action: string; status: string; database: string; target: string; rowCount: number; completeness: string }
interface BrowserRequest { method: string; action: string; path: string; body: Record<string, unknown>; usesReaderToken: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: 10_000 });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_DOCUMENT_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_DOCUMENT_REAL_BASE_URL is required; run the isolated real Document runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Document Server must be an explicit isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB26 isolated evidence', username: 'wb26_admin', password,
    bearerToken: `wb26_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator.isSuperuser).toBe(true);
  expect(administrator.username).toBe('wb26_admin');
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  reader = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(reader.username).toBe(username);
  expect(reader.isSuperuser).toBe(false);
  expect(reader.token).toBeTruthy();
  expect(reader.tokenId).toBeTruthy();
  const created = await apiJson<{ collection: string; status: string }>(request, 'POST', documentPath, {}, 201, administrator.token);
  expect(created).toMatchObject({ collection, status: 'created' });
  // Exactly one seed batch for all three tests; the isolated runner owns cleanup.
  const documents = Array.from({ length: seedCount }, (_, index) => ({
    id: `Seed:${String(index).padStart(4, '0')}`,
    document: { name: `WB26:Payload:${String(index).padStart(4, '0')}`, site: `Site:${String(index).padStart(4, '0')}`, score: index },
  }));
  const inserted = await apiJson<{ inserted: number; errors?: unknown[] | null }>(request, 'POST', `${documentPath}/insert-many`, { documents, ordered: true }, 200, administrator.token);
  expect(inserted.inserted).toBe(seedCount);
  expect(inserted.errors ?? []).toEqual([]);
  const counted = await apiJson<{ count: number }>(request, 'POST', `${documentPath}/count`, {}, 200, administrator.token);
  expect(counted.count).toBe(seedCount);
});

test.beforeEach(async ({ request }) => {
  // A failed earlier assertion cannot leave the next independent test revoked.
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
});

test('real revoke rejects approved insert, latches 403, and the same token recovers only through explicit Find100', async ({ page, request }, testInfo) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const evidence = await openDocument(page);
  await queryTab(page);
  await page.getByPlaceholder('IDs, one per line').fill('Seed:0000');
  await page.getByPlaceholder('Projection JSON array or lines: name=$.path').fill('name=$.name');
  await page.getByPlaceholder('Sort JSON array or lines: $.score desc').fill('$.score desc');
  await page.getByPlaceholder('Continuation token', { exact: true }).fill('Old:CursorDraft');
  await page.getByPlaceholder('Skip', { exact: true }).fill('7');
  await surface(page).locator('.document-toolbar__limit input').fill('7');
  await surface(page).locator('.document-builder-mode').click({ timeout: 10_000 });
  await page.locator('.n-base-select-option').getByText('Raw filter JSON', { exact: true }).click({ timeout: 10_000 });
  const filter = page.getByPlaceholder('{ "path": "$.site", "op": "eq", "value": "north" }', { exact: true });
  await filter.fill('{ "path": "$.site", "op": "eq", "value": "Old:FilterDraft" }');
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: 'Documents', exact: true }).click({ timeout: 10_000 });
  await surface(page).locator('.document-tabs .n-tabs-tab[data-name="edit"]').click({ timeout: 10_000 });
  await page.getByPlaceholder('Document ID', { exact: true }).fill(deniedId);
  await surface(page).locator('.document-inspector-section textarea').fill(JSON.stringify({ name: deniedPayload, site: 'Denied:Site' }));
  await surface(page).getByRole('button', { name: 'Stage insert', exact: true }).click({ timeout: 10_000 });
  const approval = page.getByRole('dialog', { name: 'Document operation batch' });
  await expect(approval).toContainText(deniedId);
  await expect(approval).toContainText(deniedPayload);
  expect(writes(evidence)).toHaveLength(0);

  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, 'insert-one', () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click({ timeout: 10_000 }));
  expect(denied.status()).toBe(403);
  await hiddenPermissionPayload(page);
  expect(writes(evidence)).toHaveLength(1);
  expect(writes(evidence)[0]).toMatchObject({ body: { id: deniedId, document: { name: deniedPayload } }, usesReaderToken: true });
  const absent = await apiJson<FindResponse>(request, 'POST', `${documentPath}/find`, { ids: [deniedId], limit: 1 }, 200, administrator.token);
  expect(absent.documents).toEqual([]);
  expect(absent.count).toBe(0);

  const failedRecovery = await perform(page, 'find', () => recovery(page).click({ timeout: 10_000 }));
  expect(failedRecovery.status()).toBe(403);
  await hiddenPermissionPayload(page);
  await expect(recovery(page)).toBeEnabled();
  const recoveryRequest = evidence.requests.at(-1)!;
  expect(recoveryRequest).toMatchObject({ action: 'find', body: { limit: 100, skip: 0, collation: 'ordinal' }, usesReaderToken: true });
  expect(Object.keys(recoveryRequest.body).sort()).toEqual(['collation', 'limit', 'skip']);

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequestCount = evidence.requests.length;
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click({ timeout: 10_000 });
  expect((await schema).status()).toBe(200);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).toBeEnabled();
  await hiddenPermissionPayload(page);
  expect(evidence.requests).toHaveLength(lockedRequestCount);

  const restored = await perform(page, 'find', () => recovery(page).click({ timeout: 10_000 }));
  expect(restored.status()).toBe(200);
  const actual = await restored.json() as FindResponse;
  expect(actual).toMatchObject({ collection, count: 100, limit: 100, skip: 0, hasMore: true });
  expect(actual.documents).toHaveLength(100);
  expect(actual.documents[0]).toMatchObject({ id: 'Seed:0000', document: { name: firstPayload } });
  expect(evidence.requests).toHaveLength(lockedRequestCount + 1);
  expect(evidence.requests.at(-1)).toMatchObject({ action: 'find', body: { limit: 100, skip: 0, collation: 'ordinal' }, usesReaderToken: true });
  expect(Object.keys(evidence.requests.at(-1)!.body).sort()).toEqual(['collation', 'limit', 'skip']);
  await expect(surface(page)).toHaveAttribute('data-state', 'longContent');
  await expect(surface(page).locator('.document-toolbar__meta')).toContainText('100 loaded docs');
  await expect(surface(page)).toContainText(firstPayload);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await queryTab(page);
  await expect(page.getByPlaceholder('IDs, one per line')).toHaveValue('');
  await expect(page.getByPlaceholder('Projection JSON array or lines: name=$.path')).toHaveValue('');
  await expect(page.getByPlaceholder('Sort JSON array or lines: $.score desc')).toHaveValue('');
  await expect(filter).toHaveValue('');
  await expect(page.getByPlaceholder('Skip', { exact: true })).toHaveValue('0');
  await expect(surface(page).locator('.document-toolbar__limit input')).toHaveValue('100');
  await expect(page.getByPlaceholder('Continuation token', { exact: true })).not.toHaveValue('Old:CursorDraft');
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: 'Documents', exact: true }).click({ timeout: 10_000 });
  await surface(page).locator('.document-tabs .n-tabs-tab[data-name="edit"]').click({ timeout: 10_000 });
  await expect(page.getByPlaceholder('Document ID', { exact: true })).not.toHaveValue(deniedId);
  await expect(surface(page).locator('.document-inspector-section textarea')).not.toHaveValue(new RegExp(deniedPayload, 'u'));
  expect(writes(evidence)).toHaveLength(1);
  // Independently prove the restored READ grant still rejects server writes;
  // the current routed host does not derive a readonly UI prop from that grant.
  const readOnlyWrite = await request.post(new URL(`${documentPath}/insert-one`, serverOrigin).href, {
    headers: { Authorization: `Bearer ${reader.token}` }, data: { id: deniedId, document: { name: deniedPayload } },
    timeout: apiTimeout, maxRetries: 0,
  });
  const readOnlyWriteStatus = readOnlyWrite.status();
  try { expect(readOnlyWriteStatus).toBe(403); }
  finally { await readOnlyWrite.dispose(); }
  const stillAbsent = await apiJson<FindResponse>(request, 'POST', `${documentPath}/find`, { ids: [deniedId], limit: 1 }, 200, administrator.token);
  expect(stillAbsent.documents).toEqual([]);
  expect(await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, reader.token)).toBe(true);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-document-permission', { body: JSON.stringify({ requests: safeRequests(evidence), deniedStatus: denied.status(), failedRecoveryStatus: failedRecovery.status(), restoredStatus: restored.status(), restoredCount: actual.documents.length, restoredReadGrantWriteStatus: readOnlyWriteStatus, rejectedDocumentFound: false }), contentType: 'application/json' });
});

test('real Aggregate preserves the pipeline, returns 1001 rows, and exports only the 1000-row preview', async ({ page }, testInfo) => {
  const evidence = await openDocument(page);
  await queryTab(page, 'aggregate');
  const pipeline = [{ $sort: [{ path: '$.score', descending: false }] }, { $limit: 2_000 }];
  const text = JSON.stringify(pipeline);
  await surface(page).locator('.document-query-editor textarea').fill(text);
  const response = await perform(page, 'aggregate', () => surface(page).getByRole('button', { name: 'Run aggregate', exact: true }).click({ timeout: 10_000 }));
  expect(response.status()).toBe(200);
  const actual = await response.json() as { collection: string; documents: Array<Record<string, unknown>>; count: number };
  expect(actual.collection).toBe(collection);
  expect(actual.count).toBe(1_001);
  expect(actual.documents).toHaveLength(1_001);
  expect(actual.documents.at(-1)).toMatchObject({ name: 'WB26:Payload:1000', score: 1_000 });
  expect(evidence.requests.at(-1)).toMatchObject({ action: 'aggregate', body: { pipeline: [...pipeline, { $limit: 1_001 }] }, usesReaderToken: true });
  await expect(surface(page).locator('.document-query-editor textarea')).toHaveValue(text);
  await expect(surface(page).getByTestId('document-preview-budget')).toContainText('1,000');
  await expect(surface(page).getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  const exported = await exportJson(page);
  expect(exported).toHaveLength(1_000);
  expect(exported.map((row) => JSON.parse(String(row.json)))).toEqual(actual.documents.slice(0, 1_000));
  expect(JSON.stringify(exported)).not.toContain('WB26:Payload:1000');
  await expect(resultPanel(page).locator('.workbench-result-panel__alert')).toContainText('结果已截断');
  expect(await latestHistory(page, 'aggregate')).toMatchObject({ status: 'success', database, target: collection, rowCount: 1_000, completeness: 'truncated' });
  expect(writes(evidence)).toHaveLength(0);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-document-aggregate', { body: JSON.stringify({ requests: safeRequests(evidence), status: response.status(), serverRows: actual.documents.length, exportedRows: exported.length, completeness: 'truncated' }), contentType: 'application/json' });
});

test('real Distinct uses cap500 plus one sentinel and treats a full cap1000 response as unknown completeness', async ({ page }, testInfo) => {
  const evidence = await openDocument(page);
  await queryTab(page, 'distinct');
  const capInput = surface(page).getByTestId('document-distinct-limit').locator('input');
  await capInput.fill('500');
  await capInput.blur();
  const response = await perform(page, 'distinct', () => surface(page).getByRole('button', { name: 'Run distinct', exact: true }).click({ timeout: 10_000 }));
  expect(response.status()).toBe(200);
  const actual = await response.json() as { collection: string; path: string; values: unknown[] };
  expect(actual).toMatchObject({ collection, path: '$.site' });
  expect(actual.values).toHaveLength(501);
  expect(evidence.requests.at(-1)).toMatchObject({ action: 'distinct', body: { path: '$.site', limit: 501 }, usesReaderToken: true });
  expect(evidence.requests.at(-1)!.body.ids).toBeUndefined();
  const exported = await exportJson(page);
  expect(exported).toHaveLength(500);
  expect(exported.map((row) => JSON.parse(String(row.value)))).toEqual(actual.values.slice(0, 500));
  expect(exported.map((row) => JSON.parse(String(row.value)))).not.toContainEqual(actual.values[500]);
  await expect(resultPanel(page).locator('.workbench-result-panel__alert')).toContainText('结果已截断');
  expect(await latestHistory(page, 'distinct')).toMatchObject({ status: 'success', database, target: collection, rowCount: 500, completeness: 'truncated' });
  await resultPanel(page).getByTitle('关闭结果', { exact: true }).click({ timeout: 10_000 });
  await capInput.fill('1000');
  await capInput.blur();
  const fullResponse = await perform(page, 'distinct', () => surface(page).getByRole('button', { name: 'Run distinct', exact: true }).click({ timeout: 10_000 }));
  expect(fullResponse.status()).toBe(200);
  const full = await fullResponse.json() as { collection: string; path: string; values: unknown[] };
  expect(full).toMatchObject({ collection, path: '$.site' });
  expect(full.values).toHaveLength(1_000);
  expect(evidence.requests.at(-1)).toMatchObject({ action: 'distinct', body: { path: '$.site', limit: 1_000 }, usesReaderToken: true });
  expect(evidence.requests.at(-1)!.body.ids).toBeUndefined();
  await expect(surface(page).getByTestId('document-distinct-completeness')).toContainText('未知');
  await expect(surface(page).getByRole('button', { name: 'Next page', exact: true })).toBeDisabled();
  const fullExport = await exportJson(page);
  expect(fullExport).toHaveLength(1_000);
  expect(fullExport.map((row) => JSON.parse(String(row.value)))).toEqual(full.values);
  expect(await latestHistory(page, 'distinct')).toMatchObject({ status: 'unknown', database, target: collection, rowCount: 1_000, completeness: 'unknown' });
  expect(writes(evidence)).toHaveLength(0);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-document-distinct', { body: JSON.stringify({ requests: safeRequests(evidence), cappedStatus: response.status(), cappedServerRows: actual.values.length, cappedExportRows: exported.length, fullStatus: fullResponse.status(), fullServerRows: full.values.length, fullExportRows: fullExport.length, fullCompleteness: 'unknown' }), contentType: 'application/json' });
});

function surface(page: Page) { return page.getByTestId('workbench-document'); }
function recovery(page: Page) { return surface(page).getByTestId('document-recover-read-permission'); }
function resultPanel(page: Page) { return page.locator('.workbench-result-panel'); }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => entry.method !== 'GET' && !['find', 'count', 'aggregate', 'distinct'].includes(entry.action)); }
function safeRequests(evidence: BrowserEvidence) { return evidence.requests.map(({ method, action, path, usesReaderToken }) => ({ method, action, path, usesReaderToken })); }

async function openDocument(page: Page): Promise<BrowserEvidence> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    if (!path.startsWith(`${documentPath}/`)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ method: request.method(), action: path.slice(documentPath.length + 1), path,
      body: (request.postDataJSON() ?? {}) as Record<string, unknown>, usesReaderToken: request.headers().authorization === `Bearer ${reader.token}` });
  });
  await page.addInitScript(({ identity, db }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{
      id: 'wb26-real', name: 'WB26 real local Server', kind: 'managed-local', baseUrl: '/', defaultDatabase: db,
      tokenMode: 'current-session', createdAt: 1, updatedAt: 1,
    }], activeProfileId: 'wb26-real', activeDatabase: db }));
  }, { identity: { username: reader.username, token: reader.token, tokenId: reader.tokenId, isSuperuser: reader.isSuperuser }, db: database });
  const initial = page.waitForResponse(matches('find'), { timeout: responseTimeout });
  await page.goto(`/admin/app/sql?tool=document&database=${encodeURIComponent(database)}&model=document&node=${encodeURIComponent(collection)}`);
  const response = await initial;
  expect(response.status()).toBe(200);
  expect(await response.json()).toMatchObject({ collection, count: 100, limit: 100 });
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', collection);
  await expect(surface(page).locator('.document-toolbar__meta')).toContainText('100 loaded docs');
  await expect(surface(page)).toContainText(firstPayload);
  await expect(surface(page).getByRole('button', { name: 'Browse', exact: true })).toBeEnabled();
  return evidence;
}

function matches(action: string) {
  return (response: Response) => response.request().method() === 'POST'
    && decodeURIComponent(new URL(response.url()).pathname) === `${documentPath}/${action}`;
}
async function perform(page: Page, action: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(action), { timeout: responseTimeout });
  await start();
  return response;
}
async function queryTab(page: Page, name?: 'aggregate' | 'distinct'): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: '查询', exact: true }).click({ timeout: 10_000 });
  if (name) await surface(page).locator(`.document-query-editor .n-tabs-tab[data-name="${name}"]`).click({ timeout: 10_000 });
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', collection);
  await expect(surface(page).locator('.document-grid')).toHaveCount(0);
  await expect(surface(page).locator('.document-json-preview')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(firstPayload);
  await expect(surface(page)).not.toContainText(deniedPayload);
  await expect(recovery(page)).toBeVisible();
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
async function exportJson(page: Page): Promise<Array<Record<string, unknown>>> {
  if (!(await resultPanel(page).isVisible())) await page.getByTitle('查看结果', { exact: true }).click({ timeout: 10_000 });
  await expect(resultPanel(page)).toBeVisible();
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await resultPanel(page).getByTitle('Export result set as JSON', { exact: true }).click({ timeout: 10_000 });
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    const contents = await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
    const parsed = JSON.parse(contents) as unknown;
    if (!Array.isArray(parsed) || parsed.length > 1_000) throw new Error('Document preview JSON export exceeded 1000 rows.');
    return parsed as Array<Record<string, unknown>>;
  } finally { await download.delete(); }
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, {
    method, data, headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0,
  });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    return await response.json() as T;
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> {
  const response = await request.post(new URL('/v1/sql', serverOrigin).href, {
    headers: { Authorization: `Bearer ${administrator.token}` }, data: { sql }, timeout: apiTimeout, maxRetries: 0,
  });
  try {
    expect(response.status(), 'The real control-plane SQL request must succeed.').toBe(200);
    const text = await response.text();
    if (Buffer.byteLength(text, 'utf8') > 65_536) throw new Error('Control-plane response exceeded the 64 KiB evidence budget.');
    const lines = text.trim().split(/\r?\n/u);
    if (lines.length > 32) throw new Error('Control-plane response exceeded 32 NDJSON frames.');
    const frames = lines.map((line) => JSON.parse(line) as { type?: string });
    expect(frames.some((frame) => frame.type === 'error'), 'Control-plane SQL must have no error frame.').toBe(false);
    expect(frames.at(-1)?.type, 'Control-plane SQL must finish with a real end frame.').toBe('end');
  } finally { await response.dispose(); }
}
