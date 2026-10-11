import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Real routed Web UI, Vite proxy, Kestrel authorization and FullText/Document APIs.
// API setup/login installs a real session; it is not login UI or readonly host-prop
// evidence. Top-K/local pages cover the current preview, not all matching records,
// Server scan/materialization/byte/heap budgets, Studio, VS Code or release gates.
// The non-superuser session has database ADMIN: /maintenance requires ADMIN,
// whereas WRITE alone does not authorize rebuild_index. Regrant is only READ.
const database = 'wb28';
const collection = 'DeviceProfiles_Original';
const indexName = 'Search_Original';
const username = 'wb28_writer';
const password = 'Workbench28:OnlyLocal!';
const seedCount = 151;
const profileId = 'wb28-real';
const profileName = 'WB28 real local Server';
const query = 'pump alarm';
const retainedQuery = 'pump alarm DraftMustSurvive';
const analyzerPayload = 'WB28AnalyzerPriorTokenPayload';
const importId = 'Rejected:ImportDraft';
const importPayload = 'Rejected:FullTextImportPayload';
const indexKey = `${collection}.${indexName}`;
const resourceKey = `fulltext:${collection}:${indexName}`;
const fulltextPath = `/v1/db/${database}/fulltext`;
const documentPath = `/v1/db/${database}/documents/${collection}`;
const maintenancePath = `/v1/db/${database}/maintenance`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let writer: AuthIdentity;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface SearchHit { documentId: string; score: number }
interface SearchResponse { hits: SearchHit[] }
interface TokenInfo { text: string; startOffset: number; endOffset: number; positionIncrement: number }
interface AnalyzeResponse { tokens: TokenInfo[] }
interface FindResponse {
  collection: string;
  documents: Array<{ id: string; document: Record<string, unknown>; version: number }>;
  count: number;
  limit: number;
  skip: number;
  hasMore: boolean;
}
interface IndexStat { collection: string; name: string; fields: string[]; tokenizer: string; documentCount: number; termCount: number }
interface MaintenanceResponse {
  operation: string; status: string; success: boolean; completedUtc: string;
  checks: Array<{ name: string; status: string; message: string }>;
  index: { model: string; owner: string; name: string; kind: string; mode: string; planned: boolean; rebuildable: boolean; documentCount: number };
}
interface HistoryEntry {
  action: string; status: string; database: string; target: string; connectionId: string; connectionName: string;
  command: string; rowCount: number; recordsAffected: number; completeness: string;
}
interface BrowserRequest { method: string; path: string; body: Record<string, unknown>; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: 10_000 });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_FULLTEXT_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_FULLTEXT_REAL_BASE_URL is required; use the isolated real FullText runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real FullText Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB28 isolated evidence', username: 'wb28_admin', password,
    bearerToken: `wb28_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb28_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await adminSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`, '/v1/sql');
  await controlSql(request, `GRANT ADMIN ON DATABASE ${database} TO ${username}`);
  writer = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(writer).toMatchObject({ username, isSuperuser: false });
  expect(writer.token).toBeTruthy();
  expect(writer.tokenId).toBeTruthy();
  const created = await apiJson(request, 'POST', documentPath, {}, 201, administrator.token);
  expect(created).toMatchObject({ collection, status: 'created' });
  // One bounded seed batch shared by three serial tests; the isolated runner
  // owns its Server content and process-tree cleanup.
  const documents = Array.from({ length: seedCount }, (_, position) => ({ id: seedId(position), document: seedDocument(position) }));
  const inserted = await apiJson<{ inserted: number; errors?: unknown[] | null }>(request, 'POST', `${documentPath}/insert-many`, { documents, ordered: true }, 200, administrator.token);
  expect(inserted.inserted).toBe(seedCount);
  expect(inserted.errors ?? []).toEqual([]);
  await adminSql(request, `CREATE FULLTEXT INDEX ${indexName} ON ${collection} ('$.body') USING unicode`);
  const counted = await apiJson<{ count: number }>(request, 'POST', `${documentPath}/count`, {}, 200, administrator.token);
  expect(counted.count).toBe(seedCount);
  await assertAdministratorIndex(request);
});

test.beforeEach(async ({ request }) => {
  // Prevent an earlier failed revoke assertion from poisoning another test.
  await controlSql(request, `GRANT ADMIN ON DATABASE ${database} TO ${username}`);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false });
  }
});

test('real Top-K20/100 uses search-preview, exact Find IDs and Analyzer, and exports only the current preview', async ({ page }) => {
  const evidence = await openFullText(page);
  const first = await search(page, 20);
  assertSearch(first, 20);
  await expect(surface(page).locator('.fulltext-pager')).toContainText('1-10 of 20 hits · page 1/2');
  await expect(surface(page).locator('.fulltext-doc-button')).toHaveCount(10);
  await expect(surface(page).locator('.fulltext-document pre')).toContainText('Pump alarm');
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  const requestsBeforePage = evidence.requests.length;
  await surface(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(surface(page).locator('.fulltext-pager')).toContainText('11-20 of 20 hits · page 2/2');
  expect(evidence.requests).toHaveLength(requestsBeforePage);
  const firstExport = await exportJson(page);
  assertExport(firstExport, first.preview.hits);
  await closeResult(page);

  const second = await search(page, 100);
  assertSearch(second, 100);
  await expect(surface(page).locator('.fulltext-pager')).toContainText('1-10 of 100 hits · page 1/10');
  const exported = await exportJson(page);
  assertExport(exported, second.preview.hits);
  expect(exported).toHaveLength(100);
  expect(exported.length).toBeLessThan(seedCount);
  const history = await latestHistory(page, 'search');
  expect(history).toMatchObject({ status: 'success', database, target: indexKey, connectionId: profileId,
    connectionName: profileName, rowCount: 100, completeness: 'complete' });
  expect(history?.command).toContain(collection);
  expect(history?.command).toContain(indexName);
  expect(history?.command).toContain(query);
  const previews = evidence.requests.filter((entry) => entry.path === `${fulltextPath}/search-preview`);
  expect(previews.map((entry) => entry.body.topK)).toEqual([20, 100]);
  expect(previews.every((entry) => entry.usedSession && entry.body.collection === collection && entry.body.index === indexName
    && entry.body.field === '*' && entry.body.mode === 'exact' && entry.body.queryKind === 'all' && entry.body.query === query)).toBe(true);
  expect(writes(evidence)).toHaveLength(0);
  expect(evidence.overflow).toBe(false);
  await persistEvidence('real-fulltext-topk-export', { requests: evidence.requests, statuses: first.statuses,
    firstPreview: first.preview, firstDocuments: first.documents, firstAnalysis: first.analysis, firstExport,
    secondStatuses: second.statuses, secondPreview: second.preview, secondDocuments: second.documents,
    secondAnalysis: second.analysis, exported, history,
    scope: 'Top-K current preview completed; local paging/export do not assert all matches or Server resource budgets.' });
});

test('database ADMIN rebuild approval is consumed once and validates the real synchronous document FullText terminal', async ({ page, request }) => {
  const evidence = await openFullText(page);
  await surface(page).getByRole('button', { name: 'Rebuild', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'FullText index rebuild' });
  await expect(approval).toBeVisible();
  await expect(approval).toContainText(`${database}.${indexKey}`);
  await expect(approval).toContainText(`rebuild_index document_fulltext ${indexKey}`);
  expect(writes(evidence)).toHaveLength(0);
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  const indexes = page.waitForResponse(matches(`${fulltextPath}/indexes`), { timeout: responseTimeout });
  const response = await perform(page, maintenancePath, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(response.status()).toBe(200);
  const terminal = await response.json() as MaintenanceResponse;
  expect(terminal).toMatchObject({ operation: 'rebuild_index', status: 'ok', success: true,
    index: { model: 'document', owner: collection, name: indexName, kind: 'fulltext', mode: 'sync_touch',
      planned: false, rebuildable: true, documentCount: seedCount } });
  expect(Number.isFinite(Date.parse(terminal.completedUtc))).toBe(true);
  expect(Number.isSafeInteger(terminal.index.documentCount) && terminal.index.documentCount >= 0).toBe(true);
  expect(terminal.checks).toContainEqual(expect.objectContaining({ name: 'index', status: 'ok' }));
  expect((await schema).status()).toBe(200);
  expect((await indexes).status()).toBe(200);
  await expect(approval).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).not.toHaveClass(/n-button--loading/u);
  const history = await latestHistory(page, 'rebuild');
  expect(history).toMatchObject({ status: 'success', database, target: indexKey, connectionId: profileId,
    connectionName: profileName, recordsAffected: seedCount, completeness: 'complete' });
  expect(history?.command).toBe(`rebuild_index document_fulltext ${indexKey}`);
  expect(writes(evidence)).toHaveLength(1);
  expect(writes(evidence)[0]).toMatchObject({ path: maintenancePath, usedSession: true,
    body: { operation: 'rebuild_index', targetModel: 'document_fulltext', targetOwner: collection, targetName: indexName } });
  const administratorIndex = await assertAdministratorIndex(request);
  const stored = await apiJson<FindResponse>(request, 'POST', `${documentPath}/find`, { ids: [seedId(0), seedId(150)], limit: 2 }, 200, administrator.token);
  expect(stored.collection).toBe(collection);
  expect(stored.documents.map((entry) => entry.id).sort()).toEqual([seedId(0), seedId(150)].sort());
  expect(stored.documents.map(({ id, document }) => ({ id, document })))
    .toEqual(stored.documents.map(({ id }) => ({ id, document: seedDocument(Number(id.slice(-4))) })));
  const adminSearch = await apiJson<SearchResponse>(request, 'POST', `${fulltextPath}/search-preview`, {
    collection, index: indexName, field: '*', query, topK: 100, mode: 'exact', queryKind: 'all',
  }, 200, administrator.token);
  expect(adminSearch.hits).toHaveLength(100);
  // Refresh can replace activeIndex and clear the transient result drawer;
  // persisted original-context history and the actual terminal remain evidence.
  await expect(page.getByRole('dialog')).toHaveCount(0);
  expect(writes(evidence)).toHaveLength(1);
  expect(evidence.overflow).toBe(false);
  await persistEvidence('real-fulltext-approved-rebuild', { requests: evidence.requests, status: response.status(), terminal,
    history, administratorIndex, administratorDocuments: stored, administratorSearch: adminSearch,
    writeRequests: writes(evidence).length, databaseGrant: 'ADMIN', isSuperuser: false,
    approvalConsumed: true, resultMayBeClearedBySchemaRefresh: true });
});

test('real revoke rejects old database ADMIN rebuild with HTTP403, hides payloads, and same-token READ regrant/schema refresh stays locked', async ({ page, request }) => {
  const evidence = await openFullText(page);
  const prior = await search(page, 20);
  assertSearch(prior, 20);
  await expect(surface(page).locator('.fulltext-document pre')).toContainText('Pump alarm');
  await tab(page, 'Analyzer');
  await surface(page).getByPlaceholder('Text to analyze', { exact: true }).fill(`${analyzerPayload} pump alarm`);
  const analyzed = await perform(page, `${fulltextPath}/analyze`, () => surface(page).getByRole('button', { name: 'Analyze', exact: true }).click());
  expect(analyzed.status()).toBe(200);
  const priorAnalysis = await analyzed.json() as AnalyzeResponse;
  expect(priorAnalysis.tokens.some((token) => token.text.toLowerCase() === analyzerPayload.toLowerCase())).toBe(true);
  await expect(surface(page).locator('.fulltext-token-list')).toContainText(new RegExp(analyzerPayload, 'iu'));
  await tab(page, '数据导入');
  const importDraft = JSON.stringify({ _id: importId, body: importPayload });
  await surface(page).locator('.fulltext-import-panel textarea').fill(importDraft);
  await expect(surface(page).locator('.fulltext-import-panel textarea')).toHaveValue(importDraft);
  await tab(page, '全文检索');
  await surface(page).locator('.fulltext-query-editor textarea').fill(retainedQuery);
  await surface(page).getByRole('button', { name: 'Rebuild', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'FullText index rebuild' });
  await expect(approval).toContainText(`${database}.${indexKey}`);
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, maintenancePath, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(denied.status()).toBe(403);
  const rejection = await denied.json() as { code?: string; error?: string };
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  await hiddenPermissionPayload(page);
  await tab(page, 'Analyzer');
  await hiddenPermissionPayload(page);
  await tab(page, '数据导入');
  await hiddenPermissionPayload(page);
  await expect(surface(page).locator('.fulltext-import-panel')).toHaveCount(0);
  await tab(page, '全文检索');
  await expect(surface(page).locator('.fulltext-query-editor textarea')).toHaveValue(retainedQuery);
  const deniedHistory = await latestHistory(page, 'rebuild');
  expect(deniedHistory).toMatchObject({ status: 'error', database, target: indexKey, connectionId: profileId, recordsAffected: 0 });
  expect(writes(evidence)).toHaveLength(1);
  expect(writes(evidence)[0].usedSession).toBe(true);
  const absent = await apiJson<FindResponse>(request, 'POST', `${documentPath}/find`, { ids: [importId], limit: 1 }, 200, administrator.token);
  expect(absent.documents).toEqual([]);
  const administratorIndex = await assertAdministratorIndex(request);

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequestCount = evidence.requests.length;
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await surface(page).getByRole('button', { name: 'Refresh', exact: true }).click();
  expect((await schema).status()).toBe(200);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).not.toHaveClass(/n-button--loading/u);
  await hiddenPermissionPayload(page);
  await expect(surface(page).locator('.fulltext-query-editor textarea')).toHaveValue(retainedQuery);
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Rebuild', exact: true })).toBeDisabled();
  expect(evidence.requests).toHaveLength(lockedRequestCount);
  expect(writes(evidence)).toHaveLength(1);
  const sameToken = await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, writer.token);
  expect(sameToken).toBe(true);
  const stillAbsent = await apiJson<FindResponse>(request, 'POST', `${documentPath}/find`, { ids: [importId], limit: 1 }, 200, administrator.token);
  expect(stillAbsent.documents).toEqual([]);
  expect(evidence.overflow).toBe(false);
  await persistEvidence('real-fulltext-revoked-approval', { requests: evidence.requests, deniedStatus: denied.status(),
    rejection, priorPreview: prior.preview, priorAnalysis, deniedHistory, administratorIndex,
    importDocumentFound: false, importDocumentFoundAfterRefresh: false, originalDatabaseGrant: 'ADMIN',
    isSuperuser: false, regrant: 'READ', schemaStatus: 200,
    queryPreserved: true, hitDocumentAnalyzerResultImportSurfacesHidden: true, approvalConsumed: true,
    sameSession: sameToken, stillLocked: true, writeRequests: writes(evidence).length,
    scope: 'Explicit recovery is a separate slice; READ regrant/schema refresh cannot unlock or replay old approval.' });
});

function surface(page: Page) { return page.getByTestId('workbench-fulltext'); }
function resultPanel(page: Page) { return page.locator('.workbench-result-panel'); }
function seedId(position: number) { return `Seed:${String(position).padStart(4, '0')}`; }
function seedDocument(position: number) { return { title: `DeviceProfile_${String(position).padStart(4, '0')}`, body: `Pump alarm in north station WB28 payload ${String(position).padStart(4, '0')}` }; }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => entry.path === maintenancePath || entry.path === `${documentPath}/insert-many`); }
async function tab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}

async function openFullText(page: Page): Promise<BrowserEvidence> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  const paths = [`${fulltextPath}/search-preview`, `${fulltextPath}/analyze`, `${documentPath}/find`, `${documentPath}/insert-many`, maintenancePath];
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    if (request.method() !== 'POST' || !paths.includes(path)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ method: request.method(), path, body: (request.postDataJSON() ?? {}) as Record<string, unknown>,
      usedSession: request.headers().authorization === `Bearer ${writer.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: writer, db: database, id: profileId, name: profileName });
  const indexes = page.waitForResponse(matches(`${fulltextPath}/indexes`), { timeout: responseTimeout });
  const queryString = new URLSearchParams({ tool: 'fulltext', database, model: 'fulltext', node: indexKey });
  await page.goto(`/admin/app/sql?${queryString}`);
  expect((await indexes).status()).toBe(200);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', resourceKey);
  await expect(surface(page)).toHaveAttribute('data-legacy-key', resourceKey);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'normal');
  await expect(surface(page).getByTestId('fulltext-context')).toContainText(`${database} · ${collection} · ${indexName}`);
  await expect(surface(page).getByRole('button', { name: 'Rebuild', exact: true })).toBeEnabled();
  return evidence;
}

function matches(path: string) {
  return (response: Response) => response.request().method() === 'POST' && decodeURIComponent(new URL(response.url()).pathname) === path;
}
async function perform(page: Page, path: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(path), { timeout: responseTimeout });
  await start();
  return response;
}
async function search(page: Page, topK: number): Promise<{ preview: SearchResponse; documents: FindResponse; analysis: AnalyzeResponse; statuses: number[] }> {
  await surface(page).locator('.fulltext-toolbar__topk input').fill(String(topK));
  await surface(page).locator('.fulltext-toolbar__topk input').blur();
  await surface(page).locator('.fulltext-query-editor textarea').fill(query);
  const find = page.waitForResponse(matches(`${documentPath}/find`), { timeout: responseTimeout });
  const analyze = page.waitForResponse(matches(`${fulltextPath}/analyze`), { timeout: responseTimeout });
  const response = await perform(page, `${fulltextPath}/search-preview`, () => surface(page).getByRole('button', { name: 'Search', exact: true }).click());
  const found = await find;
  const analyzed = await analyze;
  const statuses = [response.status(), found.status(), analyzed.status()];
  expect(statuses).toEqual([200, 200, 200]);
  const preview = await response.json() as SearchResponse;
  const documents = await found.json() as FindResponse;
  const analysis = await analyzed.json() as AnalyzeResponse;
  const findBody = found.request().postDataJSON() as { ids: string[]; limit: number };
  expect(findBody).toEqual({ ids: preview.hits.map((hit) => hit.documentId), limit: topK });
  expect(analyzed.request().postDataJSON()).toEqual({ tokenizer: 'unicode', text: query });
  await expect(surface(page).getByRole('button', { name: 'Search', exact: true })).not.toHaveClass(/n-button--loading/u);
  return { preview, documents, analysis, statuses };
}
function assertSearch(result: { preview: SearchResponse; documents: FindResponse; analysis: AnalyzeResponse }, topK: number): void {
  expect(result.preview.hits).toHaveLength(topK);
  expect(new Set(result.preview.hits.map((hit) => hit.documentId)).size).toBe(topK);
  expect(result.preview.hits.every((hit) => /^Seed:\d{4}$/u.test(hit.documentId) && Number.isFinite(hit.score) && hit.score > 0)).toBe(true);
  expect(result.documents).toMatchObject({ collection, count: topK, limit: topK, skip: 0, hasMore: false });
  expect(result.documents.documents).toHaveLength(topK);
  expect(result.documents.documents.map((document) => document.id).sort()).toEqual(result.preview.hits.map((hit) => hit.documentId).sort());
  expect(result.documents.documents.every((document) => Number.isSafeInteger(document.version) && document.version > 0)).toBe(true);
  expect(result.documents.documents.map(({ id, document }) => ({ id, document })))
    .toEqual(result.documents.documents.map(({ id }) => ({ id, document: seedDocument(Number(id.slice(-4))) })));
  expect(result.analysis.tokens.map((token) => token.text.toLowerCase())).toEqual(['pump', 'alarm']);
  expect(result.analysis.tokens.every((token) => Number.isSafeInteger(token.startOffset) && token.startOffset >= 0
    && Number.isSafeInteger(token.endOffset) && token.endOffset > token.startOffset && Number.isSafeInteger(token.positionIncrement))).toBe(true);
}
function assertExport(exported: Array<Record<string, unknown>>, hits: SearchHit[]): void {
  expect(exported).toHaveLength(hits.length);
  expect(exported.map((row) => ({ rank: row.rank, documentId: row.document_id, score: row.score, field: row.field })))
    .toEqual(hits.map((hit, position) => ({ rank: position + 1, documentId: hit.documentId, score: hit.score, field: '*' })));
  expect(exported.every((row) => typeof row.snippet === 'string' && row.snippet.includes('Pump alarm'))).toBe(true);
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page).getByTestId('fulltext-permission-lock')).toBeVisible();
  await expect(surface(page).locator('.fulltext-grid .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-document')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-token-list strong')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-inspector')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-stats')).toHaveCount(0);
  await expect(surface(page).locator('.fulltext-import-panel')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seedId(0));
  await expect(surface(page)).not.toContainText(analyzerPayload.toLowerCase());
  await expect(surface(page)).not.toContainText(importPayload);
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
async function closeResult(page: Page): Promise<void> {
  if (await resultPanel(page).isVisible()) await resultPanel(page).getByTitle('关闭结果', { exact: true }).click();
}
async function exportJson(page: Page): Promise<Array<Record<string, unknown>>> {
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('sndb:toggle-result', { detail: { open: true } })));
  await expect(resultPanel(page)).toBeVisible();
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await resultPanel(page).getByTitle('Export result set as JSON', { exact: true }).click();
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    const text = await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
    const parsed = JSON.parse(text) as unknown;
    if (!Array.isArray(parsed) || parsed.length > 100) throw new Error('FullText preview export exceeded 100 rows.');
    return parsed as Array<Record<string, unknown>>;
  } finally { await download.delete(); }
}
async function assertAdministratorIndex(request: APIRequestContext): Promise<IndexStat> {
  const response = await apiJson<{ indexes: IndexStat[] }>(request, 'POST', `${fulltextPath}/indexes`, undefined, 200, administrator.token);
  expect(response.indexes).toHaveLength(1);
  const index = response.indexes[0];
  expect(index).toMatchObject({ collection, name: indexName, fields: ['$.body'], tokenizer: 'unicode', documentCount: seedCount });
  expect(Number.isSafeInteger(index.termCount) && index.termCount > 0).toBe(true);
  return index;
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    return await response.json() as T;
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> { await adminSql(request, sql, '/v1/sql'); }
async function adminSql(request: APIRequestContext, sql: string, path = `/v1/db/${database}/sql`): Promise<void> {
  const response = await request.post(new URL(path, serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows: 200 }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), 'The real administrator SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    const text = await response.text();
    if (Buffer.byteLength(text, 'utf8') > 262_144) throw new Error('Setup SQL exceeded 256 KiB.');
    const lines = text.trim().split(/\r?\n/u);
    if (lines.length > 1_024) throw new Error('Setup SQL exceeded 1024 frames.');
    const frames = lines.map((line) => JSON.parse(line) as { type?: string; error?: string; code?: string; message?: string });
    expect(frames.filter((frame) => frame.type === 'error' || (typeof frame.message === 'string'
      && (typeof frame.error === 'string' || typeof frame.code === 'string')))).toEqual([]);
    expect(frames.at(-1)?.type).toBe('end');
  } finally { await response.dispose(); }
}

function samePath(left: string, right: string): boolean {
  return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right;
}
async function initializeEvidenceRoot(): Promise<void> {
  const configured = process.env.SONNETDB_FULLTEXT_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_FULLTEXT_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const parent = await realpath(resolve(process.cwd(), '..', 'artifacts', 'wb28-validation-20261006'));
  if (!samePath(dirname(root), parent) || !/^fulltext-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be a fulltext-real run immediately inside artifacts/wb28-validation-20261006.');
  }
  const runInfo = await readFile(join(root, 'run.json'), { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  if (Buffer.byteLength(runInfo, 'utf8') > 65_536) throw new Error('Runner marker exceeded 64 KiB.');
  const marker = JSON.parse(runInfo) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'fulltext-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('FullText evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const text = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, collection, index: indexName, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(text, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('FullText evidence exceeded 1 MiB/file or 8 MiB/run.');
  if ([password, administrator?.token, writer?.token, administrator?.tokenId, writer?.tokenId].filter(Boolean).some((secret) => text.includes(secret))) {
    throw new Error('Credential material must never be written to FullText evidence.');
  }
  await writeFile(target, text, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(text).digest('hex') });
}
