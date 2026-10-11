import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Locator, type Page, type Request, type Response } from '@playwright/test';
import type { ObjectBucketResponse, ObjectInfoResponse, ObjectListResponse } from '../src/api/objectStorage';

// Normal routed Vue -> real Vite proxy -> isolated fresh Kestrel, with a real
// ordinary non-superuser login. No mock, component/program-entry harness or
// forced click. List windows, bounded Range and full download are different
// contracts; Download has no product history entry. This does not prove file,
// image, Multipart, native-host, recovery, transport-budget or AOT acceptance.
const database = 'wb37';
const bucket = 'devicebucket-original'; // Server requires a lowercase DNS-style bucket name.
const username = 'wb37_operator';
const password = 'Workbench37:OnlyLocal!';
const profileId = 'wb37-real';
const profileName = 'WB37 real local Server';
const bucketPath = `/v1/db/${database}/s3/${bucket}`;
const objectCount = 151;
const contentType = 'text/plain';
const approvedKey = 'Write:Original/Approved_Original.txt';
const rejectedKey = 'Write:Original/Rejected_Original.txt';
const approvedText = 'WB37ApprovedTextPayload_Original\nA normal once-approved text object.\n';
const rejectedText = 'WB37RejectedTextDraftMustDisappear_Original';
const approvedMetadata = { origin: 'WB37Approved_Original', reviewer: 'Original' };
const approvedTags = { Class: 'Original', Purpose: 'WB37Write' };
const rejectedMetadata = { origin: 'WB37Rejected_Original', reviewer: 'RejectedDraftMustDisappear' };
const rejectedTags = { Class: 'RejectedOriginal', Purpose: 'WB37RejectedDraft' };
const apiTimeout = 10_000;
const responseTimeout = 15_000;
const seedTimeout = 45_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let seedElapsedMs = 0;
let administrator: AuthIdentity;
let operator: AuthIdentity;
let approvedObject: ObjectInfoResponse;
const seeded: ObjectInfoResponse[] = [];
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface BrowserRequest {
  method: string; path: string; query: Record<string, string>; usedSession: boolean; status?: number;
  headers: Record<string, string>; bodyObservation: 'available' | 'unavailable' | 'none';
  body: string | null; bodyBytes: number | null; bodySha256: string | null;
}
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }
interface HistoryEntry {
  title: string; action: string; model: string; status: string; database: string; target: string; connectionId: string;
  connectionName: string; command: string; summary: string; rowCount?: number; recordsAffected?: number; completeness?: string;
}
interface ByteEvidence { status: number; headers: Record<string, string>; bytes: number; sha256: string; text: string }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_OBJECT_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_OBJECT_REAL_BASE_URL is required; use the isolated real Object runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Object Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB37 isolated evidence', username: 'wb37_admin', password,
    bearerToken: `wb37_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb37_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  operator = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(operator).toMatchObject({ username, isSuperuser: false });
  expect(operator.token).toBeTruthy();
  expect(operator.tokenId).toBeTruthy();
  const created = await apiJson<ObjectBucketResponse>(request, 'PUT', bucketPath,
    { purpose: 'WB37Original real serial Object journeys' }, 200, administrator.token);
  expect(created.name).toBe(bucket);
  const seedStarted = Date.now();
  const deadline = seedStarted + seedTimeout;
  // Tiny one-item trial precedes the remaining bounded serial seed batch. The
  // exit conditions use comparisons, every request has the remaining deadline,
  // maxRetries=0, and cancellation follows the Playwright hook/test timeout.
  seeded.push(await seedObject(request, 0, deadline));
  for (let index = 1; index < objectCount && Date.now() < deadline; index += 1) {
    seeded.push(await seedObject(request, index, deadline));
    if (index % 50 === 0) console.info(`WB37 Object seed: ${index + 1}/${objectCount}`);
  }
  seedElapsedMs = Date.now() - seedStarted;
  expect(seeded).toHaveLength(objectCount);
  expect(seedElapsedMs).toBeLessThanOrEqual(seedTimeout);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], expectedFiles: 3, totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false,
      timeoutsMs: { test: 120_000, controlApi: apiTimeout, seed: seedTimeout, browserResponseAndDownloadEvent: responseTimeout, productAxios: 30_000 },
      scope: 'Object list windows, Range and download are separate current-version observations; one text PUT and one denied old text approval.' });
  }
});

test('ordinary READ retains151 keys through real100/51 continuation windows, exact version-bound206 Range and full200 browser download with actual list/Range history', async ({ page, request }) => {
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const { evidence, initial } = await openBucket(page);
  assertList(initial, seeded.slice(0, 100), true);
  expect(initial.continuationToken ?? '').toBe('');
  expect(initial.nextContinuationToken).toBeTruthy();
  await expect(surface(page).locator('.object-pager')).toContainText('Loaded 100 objects. Continuation token is ready');
  const firstHistory = await latestHistory(page, 'Object list');
  assertListHistory(firstHistory, 100);
  const button = surface(page).getByRole('button', { name: 'Load more', exact: true });
  await expect(button).toBeEnabled();
  const response = await perform(page, 'GET', '', () => button.click(), (candidate) => new URL(candidate.url()).searchParams.has('continuation-token'));
  const second = await actualJson<ObjectListResponse>(response, 200);
  expect(new URL(response.request().url()).searchParams.get('continuation-token')).toBe(initial.nextContinuationToken);
  expect(new URL(response.request().url()).searchParams.get('max-keys')).toBe('100');
  expect(second.continuationToken).toBe(initial.nextContinuationToken);
  assertList(second, seeded.slice(100), false);
  expect(second.nextContinuationToken ?? '').toBe('');
  await expect(objectKeys(page)).toHaveCount(objectCount);
  expect(await objectKeys(page).allTextContents()).toEqual(seeded.map((info) => info.key));
  await expect(surface(page).locator('.object-toolbar__meta')).toContainText(`${objectCount} loaded objects`);
  await expect(surface(page).locator('.object-pager')).toContainText('Loaded 151 objects. End of current list window.');
  await expect(button).toBeDisabled();
  await expect(surface(page).getByTestId('object-preview-budget')).toContainText('当前预览不完整。');
  const secondHistory = await latestHistory(page, 'Object list');
  assertListHistory(secondHistory, 51);
  const resultWindow = await assertVisibleResultWindow(page, second.objects);
  const range = await selectAndReadRange(page);
  const rangeHistory = await latestHistory(page, 'Object Range');
  assertRangeHistory(rangeHistory);
  const beforeDownload = await historyEntries(page);
  const download = await downloadSelected(page);
  const afterDownload = await historyEntries(page);
  expect(afterDownload).toEqual(beforeDownload);
  expect(afterDownload.filter((entry) => entry.model === 'object' && /download/iu.test(entry.title + entry.action))).toEqual([]);
  expect(new URL(page.url()).searchParams.get('node')).toBe(bucket);
  expect(writes(evidence)).toHaveLength(0);
  assertEvidence(evidence);
  await persistEvidence('real-object-read-list-range-download', { requests: evidence.requests, seedElapsedMs, seedObjectCount: seeded.length, seedPutObjects: seeded,
    initial, second, accumulatedKeys: await objectKeys(page).allTextContents(), resultWindow,
    selectedObject: seeded[0], range, download, firstHistory, secondHistory, rangeHistory, historyAfterDownload: afterDownload,
    databaseGrant: 'READ', isSuperuser: false, downloadHasHistoryEntry: false,
    scope: 'Stable fixture gives actual100/51 windows and151 retained keys. Latest result has51 rows and remains truncated because a continuation was used; no complete snapshot or transport budget claim. Download is independent full bytes and has no history.' });
});

test('ordinary WRITE approves one normal visible text upload and compares complete200 ObjectInfo/hash/version to independent administrator bytes and actual batch history', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence } = await openBucket(page);
  await fillTextUpload(page, approvedKey, approvedText, approvedMetadata, approvedTags);
  await stageTextUpload(page, approvedKey);
  expect(writes(evidence)).toHaveLength(0);
  const response = await perform(page, 'PUT', `/${approvedKey}`, () => confirmButton(page).click());
  const terminal = await actualJson<ObjectInfoResponse>(response, 200);
  const expectedBytes = Buffer.from(approvedText, 'utf8');
  assertObject(terminal, approvedKey, expectedBytes, approvedMetadata, approvedTags);
  assertObjectHeaders(response.headers(), terminal);
  const requestBodyObservation = assertUploadRequest(response.request(), approvedKey, approvedText, approvedMetadata, approvedTags);
  await expect(page.getByText('Committed 1 object action.', { exact: true })).toBeVisible();
  await expect(approval(page)).toHaveCount(0);
  const history = await latestHistory(page, 'Object operation batch');
  assertBatchHistory(history, approvedKey, 'success');
  const administratorRead = await administratorGet(request, approvedKey, terminal);
  expect(administratorRead.text).toBe(approvedText);
  approvedObject = await administratorInfo(request, approvedKey);
  expect(approvedObject).toEqual(terminal);
  expect(writes(evidence)).toHaveLength(1);
  const observedWrite = writes(evidence)[0];
  if (observedWrite.bodyObservation === 'available') {
    expect(observedWrite.body).toBe(approvedText);
    expect(observedWrite.bodyBytes).toBe(expectedBytes.length);
    expect(observedWrite.bodySha256).toBe(sha256(expectedBytes));
  } else {
    expect(observedWrite).toMatchObject({ bodyObservation: 'unavailable', body: null, bodyBytes: null, bodySha256: null });
  }
  assertEvidence(evidence);
  await persistEvidence('real-object-approved-text-put', { requests: evidence.requests,
    approvedInput: { key: approvedKey, contentType, text: approvedText, metadata: approvedMetadata, tags: approvedTags },
    put: { status: response.status(), headers: safeHeaders(response.headers()), terminal, requestBodyObservation },
    administratorRead, administratorObject: approvedObject, history, databaseGrant: 'WRITE', isSuperuser: false,
    approvalsConsumed: 1, writeRequests: 1,
    scope: 'One visible text upload staged and confirmed once through normal controls. Playwright may not expose the normal Blob request body; unavailable is explicit. Complete PUT ObjectInfo plus independent current-object GET/list proves bytes, metadata, tags, hashes and original key/version; no file or Multipart claim.' });
});

test('real REVOKE rejects old text approval with403, removes list/selected Range/body/staged visible inputs and same-token READ/Schema200 leaves six internal tabs locked without replay', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence, initial } = await openBucket(page);
  const range = await selectAndReadRange(page);
  await fillTextUpload(page, rejectedKey, rejectedText, rejectedMetadata, rejectedTags);
  await stageTextUpload(page, rejectedKey);
  expect(writes(evidence)).toHaveLength(0);
  // The staged text itself is the visible draft evidence. Changing any further
  // input would invalidate its approval signature, so leave all inputs intact.
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  await expect(confirmButton(page)).toBeEnabled();
  const response = await perform(page, 'PUT', `/${rejectedKey}`, () => confirmButton(page).click());
  const rejection = await actualJson<Record<string, unknown>>(response, 403);
  const requestBodyObservation = assertUploadRequest(response.request(), rejectedKey, rejectedText, rejectedMetadata, rejectedTags);
  await hiddenPermissionPayload(page);
  const rejectionHistory = await latestHistory(page, 'Object operation batch');
  assertBatchHistory(rejectionHistory, rejectedKey, 'error');
  const rejectedGet = await administratorMissing(request, rejectedKey);
  const afterRejection = await administratorGet(request, approvedKey, approvedObject);
  const afterRejectionObject = await administratorInfo(request, approvedKey);
  expect(afterRejection.text).toBe(approvedText);
  expect(afterRejectionObject).toEqual(approvedObject);
  const lockedRequests = evidence.requests.length;
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const schemaStatus = await refreshRealSchema(page);
  await hiddenPermissionPayload(page);
  const tabs = ['对象浏览', '治理', '图片语义', '上传 / 下载', 'Multipart', '审计'];
  const tabDeadline = Date.now() + 30_000;
  let lockedTabs = 0;
  for (let index = 0; index < tabs.length && Date.now() < tabDeadline && !page.isClosed(); index += 1) {
    await objectTab(page, tabs[index]);
    await hiddenPermissionPayload(page);
    lockedTabs += 1;
  }
  expect(lockedTabs).toBe(6);
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const sameSession = await page.evaluate((value) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === value, operator.token);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-object-revoked-text-approval', { requests: evidence.requests, initial, selectedObject: seeded[0], range,
    stagedVisibleInput: { key: rejectedKey, contentType, text: rejectedText, metadata: rejectedMetadata, tags: rejectedTags },
    denial: { status: response.status(), headers: safeHeaders(response.headers()), rejection, requestBodyObservation }, rejectionHistory, rejectedGet,
    approvedObject, afterRejection, afterRejectionObject, databaseGrantBeforeRevoke: 'WRITE', regrant: 'READ', isSuperuser: false,
    schemaStatus, sameSession, lockedTabs: tabs, selectedBucketRequestsBeforeRegrant: lockedRequests,
    selectedBucketRequestsAfterTabs: evidence.requests.length, stillLocked: true, approvalConsumed: true, writeRequests: 1,
    listSelectionRangeBodyAndStagedVisibleInputsRemoved: true,
    scope: 'Only the old normal text approval is dispatched and denied. Its visible staged inputs are removed; a Playwright-unavailable Blob body is not claimed as observed raw bytes. Same token with fresh Schema200 and all six internal tabs stays latched; no independent unstaged/file/image/Multipart/native draft or explicit recovery claim.' });
});

function surface(page: Page) { return page.getByTestId('workbench-bucket'); }
function objectKeys(page: Page) { return surface(page).locator('.object-grid .object-key-button'); }
function approval(page: Page) { return page.getByRole('dialog', { name: 'Object bucket operation batch' }); }
function confirmButton(page: Page) { return approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }); }
function uploadForm(page: Page) { return surface(page).locator('.object-inspector-section .object-form-block').first(); }
function resultPanel(page: Page) {
  // The normal result component teleports its section; scope its own rendered
  // class and exact title instead of relying on a parent's fallthrough class.
  return page.locator('.workbench-result-panel').filter({ has: page.getByText('Object bucket result', { exact: true }) });
}
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => !['GET', 'HEAD'].includes(entry.method)); }
function seedKey(index: number) { return `Seed:Original/Item${String(index).padStart(4, '0')}_Original.txt`; }
function seedBytes(index: number) {
  return Buffer.from(index === 0 ? 'WB37RangePayload_Original|'.repeat(400).slice(0, 8192) : `WB37SeedValue_Original_${String(index).padStart(4, '0')}`, 'utf8');
}
function seedMetadata(index: number) { return { origin: 'WB37Seed_Original', ordinal: String(index).padStart(4, '0') }; }
function seedTags() { return { origin: 'WB37Seed_Original' }; }
function objectUrl(key: string) { return `${bucketPath}/${key.split('/').map((part) => encodeURIComponent(part)).join('/')}`; }
function sha256(bytes: Uint8Array | string) { return createHash('sha256').update(bytes).digest('hex'); }
function encodedTags(tags: Record<string, string>) { return Object.entries(tags).map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`).join('&'); }
function uploadHeaders(metadata: Record<string, string>, tags: Record<string, string>) {
  return { 'Content-Type': contentType, ...Object.fromEntries(Object.entries(metadata).map(([key, value]) => [`x-amz-meta-${key}`, value])), 'x-amz-tagging': encodedTags(tags) };
}
async function seedObject(request: APIRequestContext, index: number, deadline: number): Promise<ObjectInfoResponse> {
  const remaining = deadline - Date.now();
  if (remaining <= 0) throw new Error('Object seed45-second deadline expired.');
  const bytes = seedBytes(index);
  const response = await request.put(new URL(objectUrl(seedKey(index)), serverOrigin).href, {
    headers: { Authorization: `Bearer ${administrator.token}`, ...uploadHeaders(seedMetadata(index), seedTags()) },
    data: bytes, timeout: Math.min(apiTimeout, remaining), maxRetries: 0,
  });
  try {
    expect(response.status()).toBe(200);
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 65_536) throw new Error('Seed ObjectInfo exceeded64KiB.');
    const info = JSON.parse(content) as ObjectInfoResponse;
    assertObject(info, seedKey(index), bytes, seedMetadata(index), seedTags());
    assertObjectHeaders(response.headers(), info);
    return info;
  } finally { await response.dispose(); }
}
function assertObject(info: ObjectInfoResponse, key: string, bytes: Buffer, metadata: Record<string, string>, tags: Record<string, string>): void {
  expect(info).toMatchObject({ bucket, key, contentType, sizeBytes: bytes.length, isDeleteMarker: false,
    metadata, tags, sha256: sha256(bytes), eTag: `"${createHash('md5').update(bytes).digest('hex')}"` });
  expect(info.metadata).toEqual(metadata);
  expect(info.tags).toEqual(tags);
  expect(typeof info.versionId).toBe('string');
  expect(info.versionId.trim()).not.toBe('');
  expect(typeof info.createdUtc).toBe('string');
  expect(typeof info.updatedUtc).toBe('string');
  expect(Number.isFinite(Date.parse(info.createdUtc))).toBe(true);
  expect(Number.isFinite(Date.parse(info.updatedUtc))).toBe(true);
}
function assertObjectHeaders(headers: Record<string, string>, info: ObjectInfoResponse, byteCount?: number): void {
  expect(headers.etag).toBe(info.eTag);
  expect(headers['x-amz-version-id']).toBe(info.versionId);
  expect(headers['x-amz-meta-sha256']).toBe(info.sha256);
  const entries = Object.entries(info.metadata);
  if (entries.length > 16) throw new Error('Fixture metadata observation exceeds16keys.');
  const deadline = Date.now() + apiTimeout;
  let verified = 0;
  for (let index = 0; index < entries.length && Date.now() < deadline; index += 1) {
    const [key, value] = entries[index];
    expect(headers[`x-amz-meta-${key}`]).toBe(value);
    verified += 1;
  }
  expect(verified).toBe(entries.length);
  // A byte count is supplied only for GET/Range; PUT's Content-Length belongs
  // to its JSON terminal and is a separate contract.
  if (byteCount !== undefined) {
    expect(headers['content-type']).toBe(info.contentType);
    expect(headers['content-length']).toBe(String(byteCount));
  }
}
function assertList(actual: ObjectListResponse, expected: ObjectInfoResponse[], truncated: boolean): void {
  expect(actual).toMatchObject({ bucket, prefix: '', maxKeys: 100, isTruncated: truncated });
  expect(actual.objects).toEqual(expected);
}
function matches(method: string, suffix: string, extra?: (response: Response) => boolean) {
  return (response: Response) => response.request().method() === method
    && decodeURIComponent(new URL(response.url()).pathname) === `${bucketPath}${suffix}` && (!extra || extra(response));
}
async function perform(page: Page, method: string, suffix: string, start: () => Promise<unknown>, extra?: (response: Response) => boolean): Promise<Response> {
  const response = page.waitForResponse(matches(method, suffix, extra), { timeout: responseTimeout });
  await start();
  return response;
}
async function objectTab(page: Page, name: string): Promise<void> {
  const tab = surface(page).locator('.workbench-section-tabs').getByRole('button').filter({ has: page.getByText(name, { exact: true }) });
  await expect(tab).toHaveCount(1);
  await expect(tab).toBeEnabled();
  await tab.click();
}
async function openBucket(page: Page): Promise<{ evidence: BrowserEvidence; initial: ObjectListResponse }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  const observed = new Map<Request, BrowserRequest>();
  page.on('request', (request) => {
    const url = new URL(request.url());
    const path = decodeURIComponent(url.pathname);
    // Parent Explorer /s3 bucket lists may refresh separately. This recorder
    // counts only this exact selected bucket path and its descendants.
    if (path !== bucketPath && !path.startsWith(`${bucketPath}/`)) return;
    if (evidence.requests.length >= 128) { evidence.overflow = true; return; }
    const bytes = request.postDataBuffer();
    if (bytes && bytes.length > 65_536) { evidence.overflow = true; return; }
    const entry: BrowserRequest = { method: request.method(), path, query: Object.fromEntries(url.searchParams),
      usedSession: request.headers().authorization === `Bearer ${operator.token}`, headers: safeHeaders(request.headers()),
      bodyObservation: bytes ? 'available' : request.method() === 'PUT' ? 'unavailable' : 'none',
      body: bytes ? bytes.toString('utf8') : null, bodyBytes: bytes?.length ?? (request.method() === 'PUT' ? null : 0), bodySha256: bytes ? sha256(bytes) : null };
    evidence.requests.push(entry);
    observed.set(request, entry);
  });
  page.on('response', (response) => { const entry = observed.get(response.request()); if (entry) entry.status = response.status(); });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: operator, db: database, id: profileId, name: profileName });
  const receivedList = page.waitForResponse(matches('GET', '', (response) => new URL(response.url()).searchParams.get('list-type') === '2'), { timeout: responseTimeout });
  await page.goto(`/admin/app/sql?${new URLSearchParams({ tool: 'bucket', database, model: 'bucket', node: bucket })}`);
  const response = await receivedList;
  expect(new URL(response.request().url()).searchParams.get('max-keys')).toBe('100');
  const initial = await actualJson<ObjectListResponse>(response, 200);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `bucket:${bucket}`);
  await expect(surface(page).locator('.object-toolbar__title')).toHaveText(bucket);
  await expect(objectKeys(page)).toHaveCount(100);
  expect(await objectKeys(page).allTextContents()).toEqual(seeded.slice(0, 100).map((info) => info.key));
  await expect(surface(page).getByRole('button', { name: 'Browse', exact: true })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.object-version-grid')).toContainText(seeded[0].versionId);
  return { evidence, initial };
}
async function assertVisibleResultWindow(page: Page, objects: ObjectInfoResponse[]) {
  const button = page.getByTitle('查看结果', { exact: true });
  await expect(button).toBeEnabled();
  await button.click();
  const result = resultPanel(page);
  await expect(result).toBeVisible();
  const table = result.getByText('Table', { exact: true });
  await expect(table).toHaveCount(1);
  await expect(table).toBeVisible();
  await table.click();
  await expect(result.locator('.workbench-result-panel__status')).toContainText('51 rows');
  await expect(result.locator('.workbench-result-panel__status')).toContainText('preview truncated');
  await expect(result.locator('.workbench-result-panel__alert')).toContainText('结果已截断');
  await expect(result.locator('.n-data-table-tbody tr')).toHaveCount(objects.length);
  const displayedKeys = await result.locator('.n-data-table-tbody tr td:first-child').allTextContents();
  expect(displayedKeys).toEqual(objects.map((info) => info.key));
  const snapshot = { rowCount: objects.length, displayedKeys, status: await result.locator('.workbench-result-panel__status').innerText(), completeness: 'truncated' };
  await result.getByTitle('关闭结果', { exact: true }).click();
  await expect(result).not.toBeVisible();
  return snapshot;
}
async function fillNumber(input: Locator, value: number): Promise<void> {
  await input.fill(String(value));
  await input.press('Enter');
  await input.press('Tab');
  await expect(input).toHaveValue(String(value));
}
async function selectAndReadRange(page: Page): Promise<ByteEvidence> {
  await objectTab(page, '对象浏览');
  const keyButton = objectKeys(page).filter({ hasText: seeded[0].key });
  await expect(keyButton).toHaveCount(1);
  await expect(keyButton).toBeEnabled();
  await keyButton.click();
  await expect(keyButton).toHaveClass(/is-active/u);
  await expect(surface(page).locator('.object-inspector .object-panel-head__meta')).toHaveText(seeded[0].key);
  await expect(surface(page).locator('.object-version-grid')).toContainText(seeded[0].versionId);
  await fillNumber(surface(page).locator('.object-preview-controls').getByPlaceholder('Start', { exact: true }), 0);
  await fillNumber(surface(page).locator('.object-preview-controls').getByPlaceholder('Bytes', { exact: true }), 4096);
  const button = surface(page).getByRole('button', { name: 'Range read', exact: true });
  await expect(button).toBeEnabled();
  const response = await perform(page, 'GET', `/${seeded[0].key}`, () => button.click(), (candidate) => candidate.request().headers().range === 'bytes=0-4095');
  expect(response.request().headers().range).toBe('bytes=0-4095');
  expect(new URL(response.request().url()).searchParams.get('versionId')).toBe(seeded[0].versionId);
  expect(response.status()).toBe(206);
  expect(response.headers()['content-range']).toBe('bytes 0-4095/8192');
  assertObjectHeaders(response.headers(), seeded[0], 4096);
  const bytes = await response.body();
  expect(bytes).toEqual(seedBytes(0).subarray(0, 4096));
  await expect(button).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.object-preview')).toHaveText(bytes.toString('utf8'));
  const preview = await surface(page).locator('.object-preview').innerText();
  expect(Buffer.byteLength(preview, 'utf8')).toBeLessThanOrEqual(4096);
  await expect(surface(page).getByTestId('object-range-budget')).toContainText('Range 0–4095 · 4096 字节 · 截断/局部预览');
  return { status: response.status(), headers: safeHeaders(response.headers()), bytes: bytes.length, sha256: sha256(bytes), text: preview };
}
async function downloadSelected(page: Page) {
  const button = surface(page).locator('.object-grid-tools').getByRole('button', { name: 'Download', exact: true });
  await expect(button).toBeEnabled();
  const receivedResponse = page.waitForResponse(matches('GET', `/${seeded[0].key}`, (candidate) => !candidate.request().headers().range), { timeout: responseTimeout });
  const receivedDownload = page.waitForEvent('download', { timeout: responseTimeout });
  await button.click();
  const response = await receivedResponse;
  expect(response.status()).toBe(200);
  expect(response.request().headers().range).toBeUndefined();
  expect(new URL(response.request().url()).searchParams.get('versionId')).toBe(seeded[0].versionId);
  assertObjectHeaders(response.headers(), seeded[0], 8192);
  const bytes = await response.body();
  expect(bytes).toEqual(seedBytes(0));
  const download = await receivedDownload;
  try {
    expect(await download.failure()).toBeNull();
    expect(download.suggestedFilename()).toBe(basename(seeded[0].key));
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download requires an absolute path.');
    const info = await lstat(file);
    if (!info.isFile() || info.isSymbolicLink() || info.size > 1_048_576) throw new Error('Browser download must be an ordinary file within1MiB.');
    const downloaded = await readFile(file, { signal: AbortSignal.timeout(apiTimeout) });
    expect(downloaded).toEqual(bytes);
    expect(sha256(downloaded)).toBe(seeded[0].sha256);
    return { status: response.status(), headers: safeHeaders(response.headers()), bytes: bytes.length, sha256: sha256(bytes),
      suggestedFilename: download.suggestedFilename(), downloadedBytes: downloaded.length, downloadedSha256: sha256(downloaded), deletedInFinally: true };
  } finally { await download.delete(); }
}
async function fillTextUpload(page: Page, key: string, text: string, metadata: Record<string, string>, tags: Record<string, string>): Promise<void> {
  await objectTab(page, '上传 / 下载');
  const form = uploadForm(page);
  await expect(form.getByPlaceholder('Object key', { exact: true })).toBeVisible();
  // Initial selected tags are real seeded data. Observe the separate selected
  // tags control before staging; never change inputs after approval creation.
  await expect(surface(page).locator('.object-form-block').nth(1).getByPlaceholder('Tags key=value', { exact: true })).toHaveValue('origin=WB37Seed_Original');
  const fields: Array<[string, string]> = [['Object key', key], ['Content-Type', contentType], ['Or paste text content', text],
    ['Metadata key=value', Object.entries(metadata).map(([name, value]) => `${name}=${value}`).join('\n')],
    ['Tags key=value', Object.entries(tags).map(([name, value]) => `${name}=${value}`).join('\n')]];
  const deadline = Date.now() + 15_000;
  let filled = 0;
  for (let index = 0; index < fields.length && Date.now() < deadline && !page.isClosed(); index += 1) {
    const [name, value] = fields[index];
    const input = form.getByPlaceholder(name, { exact: true });
    await input.fill(value);
    await input.press('Tab');
    await expect(input).toHaveValue(value);
    filled += 1;
  }
  expect(filled).toBe(5);
}
async function stageTextUpload(page: Page, key: string): Promise<void> {
  const button = uploadForm(page).getByRole('button', { name: 'Stage text upload', exact: true });
  await expect(button).toBeEnabled();
  await button.click();
  await expect(approval(page)).toBeVisible();
  await expect(approval(page)).toContainText(`${database}.${bucket}`);
  await expect(approval(page)).toContainText('Put text object');
  await expect(approval(page)).toContainText(`PUT ${bucketPath}/${key}`);
  await expect(confirmButton(page)).toBeEnabled();
}
function assertUploadRequest(request: Request, key: string, text: string, metadata: Record<string, string>, tags: Record<string, string>) {
  expect(request.method()).toBe('PUT');
  expect(decodeURIComponent(new URL(request.url()).pathname)).toBe(`${bucketPath}/${key}`);
  expect(new URL(request.url()).search).toBe('');
  // Chromium/Playwright may omit normal Blob bytes from its public request
  // observation. The complete PUT terminal and independent administrator GET
  // prove the approved bytes; null is unavailable, never an empty upload.
  const body = request.postDataBuffer();
  if (body !== null) expect(body).toEqual(Buffer.from(text, 'utf8'));
  expect(request.headers()['content-type']).toBe(contentType);
  expect(request.headers()['x-amz-tagging']).toBe(encodedTags(tags));
  const entries = Object.entries(metadata);
  if (entries.length > 16) throw new Error('Upload fixture metadata exceeds16keys.');
  const deadline = Date.now() + apiTimeout;
  let verified = 0;
  for (let index = 0; index < entries.length && Date.now() < deadline; index += 1) {
    const [name, value] = entries[index];
    expect(request.headers()[`x-amz-meta-${name}`]).toBe(value);
    verified += 1;
  }
  expect(verified).toBe(entries.length);
  return { availability: body === null ? 'unavailable' : 'available', bytes: body?.length ?? null, sha256: body ? sha256(body) : null };
}
function assertHistoryIdentity(entry: HistoryEntry | undefined): void {
  expect(entry).toMatchObject({ model: 'object', database, target: bucket, connectionId: profileId, connectionName: profileName });
}
function assertListHistory(entry: HistoryEntry | undefined, rowCount: number): void {
  assertHistoryIdentity(entry);
  expect(entry).toMatchObject({ title: 'Object list', action: 'browse', status: 'success', rowCount, recordsAffected: -1,
    command: `GET ${bucketPath}?list-type=2&prefix=`, completeness: 'truncated', summary: `${rowCount} objects · 预览不完整` });
}
function assertRangeHistory(entry: HistoryEntry | undefined): void {
  assertHistoryIdentity(entry);
  expect(entry).toMatchObject({ title: 'Object Range', action: 'range', status: 'success', rowCount: 0, recordsAffected: -1,
    completeness: 'truncated', command: `GET ${database}/${bucket}/${seeded[0].key}?versionId=${seeded[0].versionId}` });
  expect(entry?.summary).toBe('Range 0–4095 · 4096 字节 · 截断/局部预览，不代表传输预算。');
}
function assertBatchHistory(entry: HistoryEntry | undefined, key: string, status: 'success' | 'error'): void {
  assertHistoryIdentity(entry);
  expect(entry).toMatchObject({ title: 'Object operation batch', command: `PUT ${bucketPath}/${key}`, status,
    action: status === 'success' ? 'Put text object' : 'confirm', rowCount: status === 'success' ? 1 : 0, recordsAffected: status === 'success' ? 1 : 0,
    summary: status === 'success' ? '1 approved; 1 started; 1 confirmed' : '当前身份没有 Object 写入权限。 1 approved; 1 started; 0 confirmed' });
}
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
  expect(evidence.requests.every((entry) => ['GET', 'PUT'].includes(entry.method))).toBe(true);
  expect(writes(evidence).every((entry) => [approvedKey, rejectedKey].some((key) => entry.path === `${bucketPath}/${key}`))).toBe(true);
}
async function historyEntries(page: Page): Promise<HistoryEntry[]> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] }).entries.slice(0, 64));
}
async function latestHistory(page: Page, title: string): Promise<HistoryEntry | undefined> { return (await historyEntries(page)).find((entry) => entry.title === title); }
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `bucket:${bucket}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('object-permission')).toBeVisible();
  await expect(surface(page).locator('.object-toolbar__meta')).toContainText('0 loaded objects');
  await expect(surface(page).locator('.object-body')).toHaveCount(0);
  await expect(objectKeys(page)).toHaveCount(0);
  await expect(surface(page).locator('.object-preview')).toHaveCount(0);
  await expect(surface(page).locator('.object-version-grid')).toHaveCount(0);
  await expect(surface(page).locator('.object-inspector-section')).toHaveCount(0);
  await expect(surface(page).getByPlaceholder('Object key', { exact: true })).toHaveCount(0);
  await expect(surface(page).getByPlaceholder('Or paste text content', { exact: true })).toHaveCount(0);
  await expect(surface(page).getByPlaceholder('Metadata key=value', { exact: true })).toHaveCount(0);
  await expect(surface(page).getByPlaceholder('Tags key=value', { exact: true })).toHaveCount(0);
  await expect(approval(page)).toHaveCount(0);
  await expect(resultPanel(page).locator('.n-data-table-tbody tr')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seeded[0].key);
  await expect(surface(page)).not.toContainText(seedBytes(0).subarray(0, 128).toString('utf8'));
  await expect(surface(page)).not.toContainText(rejectedText);
  await expect(resultPanel(page)).not.toContainText(seeded[0].key);
}
async function refreshRealSchema(page: Page): Promise<number> {
  const button = page.getByTitle('刷新资源', { exact: true });
  await expect(button).toBeEnabled();
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await button.click();
  const response = await schema;
  expect(response.status()).toBe(200);
  await expect(button.locator('svg')).not.toHaveClass(/is-spinning/u);
  return response.status();
}
function safeHeaders(headers: Record<string, string>): Record<string, string> {
  const allowed = ['content-type', 'content-length', 'content-range', 'range', 'etag', 'x-amz-version-id', 'x-amz-meta-sha256',
    'x-amz-meta-origin', 'x-amz-meta-ordinal', 'x-amz-meta-reviewer', 'x-amz-tagging', 'x-sonnetdb-contract-version', 'x-request-id'];
  return Object.fromEntries(allowed.filter((name) => headers[name] !== undefined).map((name) => [name, headers[name]]));
}
async function actualJson<T>(response: Response, status: number): Promise<T> {
  expect(response.status()).toBe(status);
  const content = await response.text();
  if (Buffer.byteLength(content, 'utf8') > 262_144) throw new Error('Object browser JSON exceeded256KiB.');
  return JSON.parse(content) as T;
}
async function administratorGet(request: APIRequestContext, key: string, info: ObjectInfoResponse): Promise<ByteEvidence> {
  const response = await request.get(new URL(objectUrl(key), serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status()).toBe(200);
    assertObjectHeaders(response.headers(), info, info.sizeBytes);
    const bytes = await response.body();
    if (bytes.length > 65_536) throw new Error('Administrator object exceeded64KiB.');
    expect(bytes.length).toBe(info.sizeBytes);
    expect(sha256(bytes)).toBe(info.sha256);
    return { status: response.status(), headers: safeHeaders(response.headers()), bytes: bytes.length, sha256: sha256(bytes), text: bytes.toString('utf8') };
  } finally { await response.dispose(); }
}
async function administratorInfo(request: APIRequestContext, key: string): Promise<ObjectInfoResponse> {
  const list = await apiJson<ObjectListResponse>(request, 'GET', `${bucketPath}?${new URLSearchParams({ 'list-type': '2', prefix: key, 'max-keys': '1' })}`, undefined, 200, administrator.token);
  expect(list.objects).toHaveLength(1);
  expect(list.objects[0].key).toBe(key);
  return list.objects[0];
}
async function administratorMissing(request: APIRequestContext, key: string) {
  const response = await request.get(new URL(objectUrl(key), serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status()).toBe(404);
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 65_536) throw new Error('Rejected-key404 exceeded64KiB.');
    return { key, status: response.status(), response: JSON.parse(content) as Record<string, unknown> };
  } finally { await response.dispose(); }
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST' | 'PUT', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return${status}`).toBe(status);
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 262_144) throw new Error('Object control JSON exceeded256KiB.');
    return JSON.parse(content) as T;
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> {
  const response = await request.post(new URL('/v1/sql', serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows: 200 }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 262_144) throw new Error('Control SQL exceeded256KiB.');
    const lines = content.trim().split(/\r?\n/u);
    if (lines.length > 1_024) throw new Error('Control SQL exceeded1024frames.');
    const frames = lines.map((line) => JSON.parse(line) as { type?: string; error?: string; code?: string; message?: string });
    expect(frames.filter((frame) => frame.type === 'error' || (typeof frame.message === 'string'
      && (typeof frame.error === 'string' || typeof frame.code === 'string')))).toEqual([]);
    expect(frames.at(-1)?.type).toBe('end');
  } finally { await response.dispose(); }
}
function samePath(left: string, right: string): boolean { return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right; }
async function initializeEvidenceRoot(): Promise<void> {
  const configured = process.env.SONNETDB_OBJECT_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The runner must provide an absolute SONNETDB_OBJECT_REAL_EVIDENCE_ROOT.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The Object runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const parents = [resolve('D:/source/SonnetDB/artifacts/wb37-validation-20261007'),
    resolve('D:/source/SonnetDB/artifacts/wb37-continuation-20261007-01')];
  const parent = parents.find((candidate) => samePath(dirname(root), candidate));
  if (!parent || !/^object-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) throw new Error('Evidence must be an object-real run directly inside a named WB37 artifact directory.');
  const parentInfo = await lstat(parent);
  if (!parentInfo.isDirectory() || parentInfo.isSymbolicLink() || !samePath(await realpath(parent), parent)) throw new Error('The Object evidence parent escaped its named ordinary directory.');
  const markerPath = join(root, 'run.json');
  const markerInfo = await lstat(markerPath);
  if (!markerInfo.isFile() || markerInfo.isSymbolicLink() || markerInfo.size > 65_536) throw new Error('Runner marker must be an ordinary file within64KiB.');
  const marker = JSON.parse(await readFile(markerPath, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) })) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'object-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) throw new Error('Evidence marker must match this Object runner/spec/isolated Server.');
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('Object evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The Object evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Object evidence escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, bucket, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('Object evidence exceeded1MiB/file or8MiB/run.');
  if ([password, administrator?.token, operator?.token, administrator?.tokenId, operator?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) throw new Error('Credential material must never be saved to Object evidence.');
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: sha256(content) });
}
