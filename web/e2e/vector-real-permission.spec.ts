import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Routed Vue -> real Vite proxy -> isolated Kestrel. A real non-superuser READ
// session is installed through API login. Top-K/export evidence covers current
// windows, not a full index snapshot, physical ANN/Recall, embedding quality,
// Server resource budgets, login UI, host readonly props or other host gates.
// A current Measurement SQL403 propagates a typed resource/generation refusal
// to the outer Vector latch. Same-token READ regrant, Schema refresh and child
// remount must retain refusal without replay; explicit recovery is separate.
const database = 'wb32';
const measurement = 'DeviceVectors_Original';
const vectorColumn = 'Embedding_Original';
const columnNames = ['time', 'DeviceID', 'Payload_Original', vectorColumn];
const resultColumns = ['rank', 'time', 'distance', 'tags', 'fields'];
const username = 'wb32_reader';
const password = 'Workbench32:OnlyLocal!';
const profileId = 'wb32-real';
const profileName = 'WB32 real local Server';
const seedCount = 151;
const seedStart = 1_780_000_000_000;
const seedTag = 'SeedVector_Original';
const rawDraft = '[0, 0, 0]';
const filterDraft = `DeviceID = '${seedTag}'`;
const searchPath = `/v1/db/${database}/vector/search-preview`;
const indexPath = `/v1/db/${database}/vector/indexes`;
const sqlPath = `/v1/db/${database}/sql`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let readerIdentity: AuthIdentity;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface KeyValue { key: string; value: string }
interface VectorHit { timestampUtc: number; distance: number; tags?: KeyValue[] | null; fields?: KeyValue[] | null }
interface VectorResponse { hits: VectorHit[] }
interface VectorIndex { measurement: string; column: string; kind: string; dimension: number; metric: string; params: KeyValue[]; rowCount: number }
interface EndFrame { type: 'end'; rowCount: number; recordsAffected: number; elapsedMs?: number; elapsedMilliseconds?: number; truncated?: boolean }
interface SqlFrames { raw: string; columns: string[]; rows: unknown[][]; ends: EndFrame[] }
interface BrowserRequest { path: string; body: Record<string, unknown>; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }
interface HistoryEntry {
  action: string; status: string; database: string; target: string; connectionId: string; connectionName: string;
  command: string; rowCount: number; recordsAffected: number; completeness: string;
}

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_VECTOR_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_VECTOR_REAL_BASE_URL is required; use the isolated real Vector runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Vector Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB32 isolated evidence', username: 'wb32_admin', password,
    bearerToken: `wb32_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb32_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  readerIdentity = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(readerIdentity).toMatchObject({ username, isSuperuser: false });
  expect(readerIdentity.token).toBeTruthy();
  expect(readerIdentity.tokenId).toBeTruthy();
  await adminSql(request, `CREATE MEASUREMENT ${measurement} (DeviceID TAG, Payload_Original FIELD STRING, ${vectorColumn} FIELD VECTOR(3) WITH INDEX hnsw(m=16, ef=200, metric='l2'))`);
  // One bounded 151-point seed. Integer components give distinct L2 distances.
  const values = Array.from({ length: seedCount }, (_, index) => `(${seedTime(index)}, '${seedTag}', '${seedPayload(index)}', [${index + 1}, 0, 0])`);
  const inserted = await adminSql(request, `INSERT INTO ${measurement} (${columnNames.join(', ')}) VALUES ${values.join(', ')}`);
  expect(inserted.ends).toHaveLength(1);
  expect(inserted.ends[0]).toMatchObject({ rowCount: 0, recordsAffected: seedCount });
  const seeded = await adminSql(request, `SELECT ${columnNames.join(', ')} FROM ${measurement} ORDER BY time ASC LIMIT 152`);
  expect(seeded.columns).toEqual(columnNames);
  expect(seeded.rows).toEqual(Array.from({ length: seedCount }, (_, index) => seedRow(index)));
  const schema = await apiJson<{ measurements: Array<{ name: string; columns: Array<{ name: string; role: string; vectorDimension?: number }> }> }>(request, 'GET', `/v1/db/${database}/schema`, undefined, 200, readerIdentity.token);
  expect(schema.measurements).toHaveLength(1);
  expect(schema.measurements[0].name).toBe(measurement);
  expect(schema.measurements[0].columns.filter((column) => column.name !== 'time').map((column) => column.name)).toEqual(columnNames.slice(1));
  expect(schema.measurements[0].columns.find((column) => column.name === vectorColumn)?.vectorDimension).toBe(3);
});

test.beforeEach(async ({ request }) => {
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false,
      childPermissionBoundary: 'A current child SQL403 clears outer Vector hits/metadata/result and locks the parent; same-token READ regrant, Schema200 and child remount cannot refill or replay.' });
  }
});

test('real raw Vector TopK20/100 preserves original names, distances/metadata, current JSON/CSV and READ identity history', async ({ page }) => {
  const { evidence, index } = await openVector(page);
  await configureSearch(page, 20);
  const firstResponse = await perform(page, searchPath, () => searchButton(page).click());
  const first = await actualSearch(firstResponse);
  assertSearchRequest(firstResponse, 20);
  assertHits(first, 20);
  await settledSearch(page, 20);
  await assertSelectedHit(page, first.hits[0], 1);
  const firstHistory = await latestHistory(page, 'search');
  assertSearchHistory(firstHistory, 20, 'success');
  const firstJson = await exportText(page, 'JSON');
  const firstCsv = await exportText(page, 'CSV');
  assertExports(firstJson, firstCsv, first.hits);
  await resultPanel(page).getByTitle('关闭结果', { exact: true }).click();

  await surface(page).locator('.vector-toolbar__topk input').fill('100');
  const largeResponse = await perform(page, searchPath, () => searchButton(page).click());
  const large = await actualSearch(largeResponse);
  assertSearchRequest(largeResponse, 100);
  assertHits(large, 100);
  await settledSearch(page, 100);
  await surface(page).locator('.vector-rank-button').filter({ hasText: /^100$/u }).click();
  await assertSelectedHit(page, large.hits[99], 100);
  const largeHistory = await latestHistory(page, 'search');
  assertSearchHistory(largeHistory, 100, 'success');
  const largeJson = await exportText(page, 'JSON');
  const largeCsv = await exportText(page, 'CSV');
  assertExports(largeJson, largeCsv, large.hits);
  expect(JSON.parse(largeJson)).toHaveLength(100);
  expect(large.hits.length).toBeLessThan(seedCount);
  await resultPanel(page).getByTitle('关闭结果', { exact: true }).click();
  await vectorTab(page, '索引参数');
  await expect(surface(page).locator('.vector-index-params')).toBeVisible();
  expect(index.params.length).toBeLessThanOrEqual(16);
  await expect(surface(page).locator('.vector-param-list small')).toHaveText(index.params.map((param) => param.key));
  await expect(surface(page).locator('.vector-param-list strong')).toHaveText(index.params.map((param) => param.value));
  expect(new URL(page.url()).searchParams.get('node')).toBe(`${measurement}.${vectorColumn}`);
  expect(evidence.requests.filter((entry) => entry.path === searchPath)).toHaveLength(2);
  assertEvidence(evidence);
  await persistEvidence('real-vector-current-topk', { requests: evidence.requests, index, first, firstHeaders: safeHeaders(firstResponse),
    firstJson, firstCsv, firstHistory, large, largeHeaders: safeHeaders(largeResponse), largeJson, largeCsv, largeHistory,
    databaseGrant: 'READ', isSuperuser: false, seededPoints: seedCount,
    scope: 'Current retained Top-K windows and declared index metadata only; no full snapshot, physical ANN/Recall or Server resource-budget claim.' });
});

test('actual search REVOKE403 clears Vector payloads and READ/same-token Schema200 keeps the outer deny latch and data child locked', async ({ page, request }) => {
  const { evidence, index } = await openVector(page);
  await configureSearch(page, 20);
  const successfulResponse = await perform(page, searchPath, () => searchButton(page).click());
  const successful = await actualSearch(successfulResponse);
  assertHits(successful, 20);
  await settledSearch(page, 20);
  const previousResult = await openResult(page);
  await previousResult.locator('.n-tabs-tab[data-name="raw"]').click();
  await expect(previousResult.locator('.workbench-result-panel__result')).toContainText(seedPayload(0));
  await previousResult.getByTitle('关闭结果', { exact: true }).click();
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, searchPath, () => searchButton(page).click());
  const rejection = await actualPermissionFailure(denied);
  await hiddenVectorPayload(page);
  await expect(rawEditor(page)).toHaveValue(rawDraft);
  await expect(surface(page).locator('.vector-filter-row input')).toHaveValue(filterDraft);
  const history = await latestHistory(page, 'search');
  assertSearchHistory(history, 0, 'error');
  expect(history?.completeness).toBeUndefined();

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequests = evidence.requests.length;
  const schemaStatus = await refreshRealSchema(page);
  await hiddenVectorPayload(page);
  await vectorTab(page, '索引参数');
  await hiddenVectorPayload(page);
  await vectorTab(page, '数据编辑 / 导入');
  await hiddenVectorPayload(page);
  await hiddenChildPayload(page, false);
  await childTab(page, 'Schema');
  await hiddenChildPayload(page, false);
  await childTab(page, '实时监控');
  await hiddenChildPayload(page, false);
  await expect(child(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u })).toBeDisabled();
  await childTab(page, '文件导入');
  await hiddenChildPayload(page, false);
  await expect(child(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据', { exact: true })).toHaveValue('');
  await expect(child(page).getByRole('button', { name: '选择文件', exact: true })).toBeDisabled();
  await vectorTab(page, '向量检索');
  await hiddenVectorPayload(page);
  await expect(searchButton(page)).toBeDisabled();
  await expect(rawEditor(page)).toHaveValue(rawDraft);
  await expect(surface(page).locator('.vector-filter-row input')).toHaveValue(filterDraft);
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(evidence.requests.filter((entry) => entry.path === searchPath)).toHaveLength(2);
  const sameSession = await hasSameSession(page);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-vector-revoked-search', { requests: evidence.requests, index, successful, deniedStatus: denied.status(),
    rejection, history, databaseGrant: 'READ', isSuperuser: false, regrant: 'READ', schemaStatus, sameSession,
    outerPermissionLocked: true, childLockedByParentProp: true, hitMetadataResultAndIndexPayloadCleared: true,
    scope: 'Search403 supplies the outer latch; same-token Schema refresh and subpage visits cannot refill or replay. Explicit recovery is separate.' });
});

test('actual child Measurement SQL403 clears the previous outer Vector result and survives READ/same-token Schema200 and child remount', async ({ page, request }) => {
  const { evidence, index } = await openVector(page);
  await configureSearch(page, 20);
  const successfulResponse = await perform(page, searchPath, () => searchButton(page).click());
  const successful = await actualSearch(successfulResponse);
  assertHits(successful, 20);
  await settledSearch(page, 20);
  const childRead = page.waitForResponse(matches(sqlPath), { timeout: responseTimeout });
  await vectorTab(page, '数据编辑 / 导入');
  const initialResponse = await childRead;
  const initial = await actualSql(initialResponse);
  expect(initialResponse.request().postDataJSON()).toEqual({
    sql: `SELECT ${columnNames.join(', ')}\nFROM ${measurement}\nORDER BY time DESC\nLIMIT @limit;`,
    parameters: { limit: { kind: 2, integerValue: 100 } }, previewMaxRows: 100,
  });
  expect(initial.columns).toEqual(columnNames);
  expect(initial.rows).toEqual(Array.from({ length: 100 }, (_, position) => seedRow(150 - position)));
  await expect(child(page)).toHaveAttribute('data-resource-key', measurement);
  await expect(child(page).locator('.measurement-statusbar')).toContainText('100 个点');
  await expect(child(page).getByRole('button', { name: /^(?:loading\s+)?查询$/u })).not.toHaveClass(/n-button--loading/u);
  await expect(child(page).locator('.measurement-grid')).toContainText(seedPayload(150));
  const initialHistory = await latestHistory(page, 'points');
  expect(initialHistory).toMatchObject({ status: 'success', database, target: measurement, connectionId: profileId,
    connectionName: profileName, rowCount: 100, recordsAffected: -1 });
  await childTab(page, '文件导入');
  const importDraft = `time,DeviceID,Payload_Original,${vectorColumn}\n${seedStart + seedCount * 1_000},ChildImportDraftMustDisappear,ChildImportPayloadMustDisappear,"[1,0,0]"`;
  await child(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据').fill(importDraft);
  await child(page).getByRole('button', { name: '解析', exact: true }).click();
  await expect(child(page).locator('.measurement-import-grid')).toContainText('ChildImportDraftMustDisappear');
  await childTab(page, '数据点');
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, sqlPath, () => child(page).getByRole('button', { name: /^(?:loading\s+)?刷新$/u }).click());
  const rejection = await actualPermissionFailure(denied);
  await hiddenVectorPayload(page);
  await hiddenChildPayload(page, false);
  const lockedRequests = evidence.requests.length;
  await childTab(page, 'Schema');
  await hiddenChildPayload(page, false);
  await childTab(page, '实时监控');
  await hiddenChildPayload(page, false);
  await expect(child(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u })).toBeDisabled();
  await childTab(page, '文件导入');
  await hiddenChildPayload(page, false);
  await expect(child(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据', { exact: true })).toHaveValue('');
  await expect(child(page).getByRole('button', { name: '选择文件', exact: true })).toBeDisabled();
  await expect(surface(page)).not.toContainText('ChildImportDraftMustDisappear');
  await expect(surface(page)).not.toContainText('ChildImportPayloadMustDisappear');
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const schemaStatus = await refreshRealSchema(page);
  await hiddenVectorPayload(page);
  await hiddenChildPayload(page, false);
  await vectorTab(page, '向量检索');
  await hiddenVectorPayload(page);
  await expect(rawEditor(page)).toHaveValue(rawDraft);
  await expect(surface(page).locator('.vector-filter-row input')).toHaveValue(filterDraft);
  await expect(searchButton(page)).toBeDisabled();
  await vectorTab(page, '索引参数');
  await hiddenVectorPayload(page);
  await vectorTab(page, '数据编辑 / 导入');
  await hiddenVectorPayload(page);
  await hiddenChildPayload(page, false);
  await childTab(page, '文件导入');
  await hiddenChildPayload(page, false);
  await expect(child(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据')).toHaveValue('');
  await expect(child(page).getByRole('button', { name: '暂存导入', exact: true })).toBeDisabled();
  await vectorTab(page, '向量检索');
  await hiddenVectorPayload(page);
  await expect(searchButton(page)).toBeDisabled();
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(evidence.requests.filter((entry) => entry.path === searchPath)).toHaveLength(1);
  expect(evidence.requests.filter((entry) => entry.path === sqlPath)).toHaveLength(2);
  const sameSession = await hasSameSession(page);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-vector-child-sql-rejection', { requests: evidence.requests, index, successful, initial, initialHistory,
    deniedStatus: denied.status(), rejection, sameSession, regrant: 'READ', schemaStatus, childPermissionLocked: true,
    childPayloadAndImportDraftCleared: true, outerPermissionLocked: true, previousOuterVectorResultCleared: true,
    outerHitMetadataAndIndexPayloadCleared: true, noFurtherRequestsAfterChildRefusal: true,
    scope: 'Current child SQL403 propagates to the outer resource/generation latch. READ regrant with same token, Schema200 and child remount cannot refill or replay. No explicit recovery or host/OS claim.' });
});

function surface(page: Page) { return page.getByTestId('workbench-vector'); }
function child(page: Page) { return surface(page).getByTestId('workbench-measurement'); }
function rawEditor(page: Page) { return surface(page).locator('.vector-query-editor textarea'); }
function searchButton(page: Page) { return surface(page).getByRole('button', { name: /^(?:loading\s+)?Search$/u }); }
function seedTime(index: number) { return seedStart + index * 1_000; }
function seedPayload(index: number) { return `WB32SeedPayload_${String(index).padStart(4, '0')}`; }
function seedRow(index: number) { return [seedTime(index), seedTag, seedPayload(index), [index + 1, 0, 0]]; }
function pairs(items?: KeyValue[] | null) { return (items ?? []).map((item) => `${item.key}=${item.value}`).join(' · '); }
function resultObjects(hits: VectorHit[]) {
  return hits.map((hit, index) => ({ rank: index + 1, time: hit.timestampUtc, distance: hit.distance, tags: pairs(hit.tags), fields: pairs(hit.fields) }));
}
function resultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({ has: page.locator('.workbench-result-panel__title').filter({ hasText: /^Vector search result$/u }) });
}
function matches(path: string) {
  return (response: Response) => response.request().method() === 'POST' && decodeURIComponent(new URL(response.url()).pathname) === path;
}
async function perform(page: Page, path: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(path), { timeout: responseTimeout });
  await start();
  return response;
}
async function vectorTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').first().getByRole('button', { name, exact: true }).click();
}
async function childTab(page: Page, name: string): Promise<void> {
  await child(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function openVector(page: Page): Promise<{ evidence: BrowserEvidence; index: VectorIndex }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    if (request.method() !== 'POST' || ![searchPath, sqlPath, `${sqlPath}/batch`, `/v1/db/${database}/vector/embed-preview`].includes(path)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ path, body: request.postDataJSON() as Record<string, unknown>,
      usedSession: request.headers().authorization === `Bearer ${readerIdentity.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: readerIdentity, db: database, id: profileId, name: profileName });
  const received = page.waitForResponse(matches(indexPath), { timeout: responseTimeout });
  await page.goto(`/admin/app/sql?${new URLSearchParams({ tool: 'vector', database, model: 'vector', node: `${measurement}.${vectorColumn}` })}`);
  const response = await received;
  expect(response.status()).toBe(200);
  const listed = await boundedJson<{ indexes: VectorIndex[] }>(response);
  expect(listed.indexes).toHaveLength(1);
  const index = listed.indexes[0];
  expect(index).toMatchObject({ measurement, column: vectorColumn, kind: 'Hnsw', dimension: 3, metric: 'l2', rowCount: seedCount });
  expect(index.params.every((param) => typeof param.key === 'string' && typeof param.value === 'string')).toBe(true);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `${measurement}:${vectorColumn}`);
  await expect(surface(page).locator('.vector-toolbar__title')).toHaveText(`${measurement}.${vectorColumn}`);
  await expect(surface(page).locator('.vector-toolbar__meta')).toContainText('dim 3');
  await expect(surface(page).locator('.vector-toolbar__meta')).toContainText(`${seedCount} rows`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'normal');
  return { evidence, index };
}
async function configureSearch(page: Page, topK: number): Promise<void> {
  await rawEditor(page).fill(rawDraft);
  await surface(page).getByRole('button', { name: 'Parse', exact: true }).click();
  await surface(page).locator('.vector-toolbar__topk input').fill(String(topK));
  await surface(page).locator('.vector-filter-row input').fill(filterDraft);
  await expect(searchButton(page)).toBeEnabled();
}
function assertSearchRequest(response: Response, topK: number): void {
  expect(response.request().postDataJSON()).toEqual({ measurement, column: vectorColumn, query: [0, 0, 0], topK, metric: 'l2', filter: filterDraft });
}
function assertHits(response: VectorResponse, count: number): void {
  expect(response.hits).toHaveLength(count);
  expect(response.hits.map((hit) => hit.timestampUtc)).toEqual(Array.from({ length: count }, (_, index) => seedTime(index)));
  expect(response.hits.map((hit) => hit.distance)).toEqual(Array.from({ length: count }, (_, index) => index + 1));
  expect(response.hits.map((hit) => hit.tags)).toEqual(Array.from({ length: count }, () => [{ key: 'DeviceID', value: seedTag }]));
  expect(response.hits.map((hit) => hit.fields)).toEqual(Array.from({ length: count }, (_, index) => [
    { key: 'Payload_Original', value: seedPayload(index) }, { key: vectorColumn, value: `[${index + 1}, 0, 0]` },
  ]));
}
async function settledSearch(page: Page, count: number): Promise<void> {
  await expect(searchButton(page)).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.vector-rank-button')).toHaveCount(count);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  await expect(surface(page).getByTestId('vector-preview-budget')).toContainText('Top-K preview only');
}
async function assertSelectedHit(page: Page, hit: VectorHit, rank: number): Promise<void> {
  await expect(surface(page).locator('.vector-inspector .vector-panel-head__meta')).toHaveText(`rank ${rank}`);
  await expect(surface(page).locator('.vector-detail-strip')).toContainText(String(hit.timestampUtc));
  await expect(surface(page).locator('.vector-kv-section pre').nth(0)).toHaveText(pairs(hit.tags));
  await expect(surface(page).locator('.vector-kv-section pre').nth(1)).toHaveText(pairs(hit.fields));
}
function assertSearchHistory(history: HistoryEntry | undefined, count: number, status: string): void {
  expect(history).toMatchObject({ action: 'search', status, database, target: `${measurement}.${vectorColumn}`,
    connectionId: profileId, connectionName: profileName, rowCount: count, recordsAffected: -1 });
  expect(history?.command).toContain(`knn(${measurement}, ${vectorColumn}, ${rawDraft}, ${count || 20}, 'l2')`);
  expect(history?.command).toContain(filterDraft);
  if (status === 'success') expect(history?.completeness).toBe('complete');
}
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
  expect(evidence.requests.filter((entry) => entry.path.endsWith('/batch') || entry.path.endsWith('/embed-preview'))).toEqual([]);
}
async function openResult(page: Page) {
  await page.getByTitle('查看结果', { exact: true }).click();
  await expect(resultPanel(page)).toBeVisible();
  return resultPanel(page);
}
async function exportText(page: Page, format: 'JSON' | 'CSV'): Promise<string> {
  const panel = resultPanel(page);
  if (!await panel.isVisible()) await openResult(page);
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await panel.getByTitle(`Export result set as ${format}`, { exact: true }).click();
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    expect(download.suggestedFilename()).toBe(`${database}_${measurement}_${vectorColumn}.${format.toLowerCase()}`);
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    return await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  } finally { await download.delete(); }
}
function assertExports(json: string, csv: string, hits: VectorHit[]): void {
  const expected = resultObjects(hits);
  expect(JSON.parse(json)).toEqual(expected);
  // Tiny parser input first verifies comma/quote escaping independently of
  // the production exporter before consuming the bounded real download.
  expect(parseCsv('a,b\n"x,y","z""w"\n')).toEqual([['a', 'b'], ['x,y', 'z"w']]);
  const rows = parseCsv(csv);
  expect(rows[0]).toEqual(resultColumns);
  expect(rows.slice(1)).toEqual(expected.map((row) => resultColumns.map((column) => String(row[column as keyof typeof row]))));
  expect(csv.endsWith('\n')).toBe(true);
}
function parseCsv(text: string): string[][] {
  if (text.length > 262_144) throw new Error('CSV evidence exceeded 256K characters.');
  const deadline = Date.now() + 10_000;
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let quoted = false;
  for (let position = 0; position < text.length && position < 262_144; position += 1) {
    if (Date.now() >= deadline) throw new Error('CSV reader exceeded10s.');
    const character = text[position];
    if (character === '"') {
      if (quoted && text[position + 1] === '"') { cell += '"'; position += 1; }
      else quoted = !quoted;
    } else if (character === ',' && !quoted) { row.push(cell); cell = ''; }
    else if (character === '\n' && !quoted) {
      row.push(cell.replace(/\r$/u, '')); rows.push(row); row = []; cell = '';
      if (rows.length > 101) throw new Error('CSV reader exceeded101 rows.');
    } else cell += character;
  }
  if (quoted) throw new Error('CSV evidence has an unterminated quote.');
  if (cell || row.length) { row.push(cell); rows.push(row); }
  if (rows.length > 101) throw new Error('CSV reader exceeded101 rows.');
  return rows;
}
async function hiddenVectorPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `${measurement}:${vectorColumn}`);
  await expect(surface(page).getByTestId('vector-permission-lock')).toBeVisible();
  await expect(surface(page).locator('.vector-rank-button')).toHaveCount(0);
  await expect(surface(page).locator('.vector-kv-section')).toHaveCount(0);
  await expect(surface(page).locator('.vector-stats')).toHaveCount(0);
  await expect(surface(page).locator('.vector-toolbar__meta')).toHaveCount(0);
  await expect(surface(page).locator('.vector-index-card')).toHaveCount(0);
  await expect(surface(page).locator('.vector-index-params')).toHaveCount(0);
  await expect(surface(page).locator('.vector-param-strip')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seedPayload(0));
  await expect(surface(page)).not.toContainText(seedPayload(150));
  await expect(page.getByRole('dialog')).toHaveCount(0);
}
async function hiddenChildPayload(page: Page, hasResource: boolean): Promise<void> {
  await expect(child(page)).toHaveAttribute('data-state', 'permission');
  await expect(child(page)).toHaveAttribute('data-database', database);
  if (hasResource) await expect(child(page)).toHaveAttribute('data-resource-key', measurement);
  await expect(child(page).getByTestId('measurement-permission-lock')).toBeVisible();
  await expect(child(page).locator('.measurement-grid .n-data-table-td')).toHaveCount(0);
  await expect(child(page).locator('.measurement-schema .n-data-table-td')).toHaveCount(0);
  await expect(child(page).locator('.monitor-grid-panel .n-data-table-td')).toHaveCount(0);
  await expect(child(page).locator('.point-editor')).toHaveCount(0);
  await expect(child(page).locator('.measurement-import-grid')).toHaveCount(0);
  await expect(child(page).locator('.measurement-approval-zone')).toBeEmpty();
  await expect(child(page).locator('.measurement-statusbar code')).toHaveCount(0);
  await expect(child(page)).not.toContainText(seedPayload(150));
}
async function refreshRealSchema(page: Page): Promise<number> {
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await page.getByTitle('刷新资源', { exact: true }).click();
  const response = await schema;
  expect(response.status()).toBe(200);
  await expect(page.getByTitle('刷新资源', { exact: true }).locator('svg')).not.toHaveClass(/is-spinning/u);
  return response.status();
}
async function hasSameSession(page: Page): Promise<boolean> {
  return page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, readerIdentity.token);
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
function safeHeaders(response: Response) {
  const headers = response.headers();
  return { contentType: headers['content-type'], contractVersion: headers['x-sonnetdb-contract-version'], requestId: headers['x-request-id'] };
}
async function boundedJson<T>(response: Response): Promise<T> {
  const raw = await response.text();
  if (Buffer.byteLength(raw, 'utf8') > 262_144) throw new Error('Vector JSON evidence exceeded256KiB.');
  return JSON.parse(raw) as T;
}
async function actualSearch(response: Response): Promise<VectorResponse> {
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('json');
  const actual = await boundedJson<VectorResponse>(response);
  expect(Array.isArray(actual.hits)).toBe(true);
  expect(actual.hits.length).toBeLessThanOrEqual(100);
  return actual;
}
async function actualPermissionFailure(response: Response): Promise<unknown> {
  expect(response.status()).toBe(403);
  const rejection = await boundedJson<{ code?: string; error?: string }>(response);
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  return rejection;
}
async function actualSql(response: Response): Promise<SqlFrames> {
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('ndjson');
  return parseFrames(await response.text());
}
function parseFrames(raw: string): SqlFrames {
  if (Buffer.byteLength(raw, 'utf8') > 262_144) throw new Error('SQL evidence exceeded256KiB.');
  const lines = raw.trim().split(/\r?\n/u);
  if (lines.length > 1_024) throw new Error('SQL evidence exceeded1024 NDJSON frames.');
  const frames = lines.map((line) => JSON.parse(line) as unknown);
  const objects = frames.filter((frame) => frame && !Array.isArray(frame) && typeof frame === 'object') as Array<{ type?: string; columns?: string[]; code?: string; error?: string; message?: string }>;
  expect(objects.filter((frame) => frame.type === 'error' || (frame.message && (frame.code || frame.error)))).toEqual([]);
  expect(objects.every((frame) => frame.type === 'meta' || frame.type === 'end')).toBe(true);
  expect(objects.at(-1)?.type).toBe('end');
  const columns = objects.find((frame) => frame.type === 'meta')?.columns ?? [];
  const rows = frames.filter(Array.isArray) as unknown[][];
  expect(rows.every((row) => row.length === columns.length)).toBe(true);
  const ends = objects.filter((frame) => frame.type === 'end') as unknown as EndFrame[];
  expect(ends.length).toBeGreaterThan(0);
  expect(ends.every((frame) => Number.isSafeInteger(frame.rowCount) && frame.rowCount >= 0
    && Number.isSafeInteger(frame.recordsAffected) && frame.recordsAffected >= -1
    && Number.isFinite(frame.elapsedMs ?? frame.elapsedMilliseconds)
    && (frame.elapsedMs ?? frame.elapsedMilliseconds ?? -1) >= 0)).toBe(true);
  expect(ends.reduce((count, frame) => count + frame.rowCount, 0)).toBe(rows.length);
  return { raw, columns, rows, ends };
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try { expect(response.status(), `${method} ${path} must return ${status}`).toBe(status); return await response.json() as T; }
  finally { await response.dispose(); }
}
async function adminSql(request: APIRequestContext, sql: string, path = sqlPath): Promise<SqlFrames> {
  const response = await request.post(new URL(path, serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows: 200 }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), 'The real administrator SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    return parseFrames(await response.text());
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> { await adminSql(request, sql, '/v1/sql'); }
function samePath(left: string, right: string): boolean {
  return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right;
}
async function initializeEvidenceRoot(): Promise<void> {
  const configured = process.env.SONNETDB_VECTOR_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_VECTOR_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const allowedParents = ['wb32-validation-20261007', 'wb33-validation-20261007'];
  const configuredParent = dirname(root);
  const matchedParent = allowedParents.find((name) => samePath(configuredParent, resolve(process.cwd(), '..', 'artifacts', name)));
  if (!matchedParent || !/^vector-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be a vector-real run immediately inside the named WB32 or WB33 validation artifact directory.');
  }
  const parent = await realpath(resolve(process.cwd(), '..', 'artifacts', matchedParent));
  if (!samePath(configuredParent, parent)) throw new Error('The evidence parent realpath escaped the named validation directory.');
  const runInfo = await readFile(join(root, 'run.json'), { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  if (Buffer.byteLength(runInfo, 'utf8') > 65_536) throw new Error('Runner marker exceeded64KiB.');
  const marker = JSON.parse(runInfo) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'vector-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('Vector evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, measurement, vectorColumn, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('Vector evidence exceeded1MiB/file or8MiB/run.');
  if ([password, administrator?.token, readerIdentity?.token, administrator?.tokenId, readerIdentity?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) {
    throw new Error('Credential material must never be written to Vector evidence.');
  }
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(content).digest('hex') });
}
