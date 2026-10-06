import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Routed Vue, the real Vite proxy and isolated Kestrel authorization/KV APIs.
// API login installs a real non-superuser WRITE session; this does not test
// login UI, readonly host props, Studio, VS Code, AOT or release gates. Scan
// windows and current-loaded JSONL are not a keyspace snapshot or Server
// scan/materialization/transfer/byte/heap budget. Full atomic/Int64/TTL/CAS and
// explicit permission recovery remain separate slices.
const database = 'wb29';
const keyspace = 'DeviceState_Original';
const username = 'wb29_writer';
const password = 'Workbench29:OnlyLocal!';
const profileId = 'wb29-real';
const profileName = 'WB29 real local Server';
const seedCount = 151;
const writeKey = 'Atomic:Original';
const firstWrite = 'WB29:NX:OriginalValue';
const ignoredWrite = 'WB29:NX:MustNotReplace';
const exchangedWrite = 'WB29:Exchange:OriginalValue';
const deniedKey = 'Rejected:Original';
const editorDraft = 'WB29:EditorDraftMustDisappear';
const importDraft = 'WB29:ImportDraftMustDisappear';
const kvPath = `/v1/db/${database}/kv/${keyspace}`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let writer: AuthIdentity;
let seedVersions: Record<string, number>;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface KvEntry { key: string; value: string; version: number; expiresAtUtc?: string | null }
interface ScanResponse { entries: KvEntry[]; nextCursor?: string | null; hasMore: boolean }
interface ValueResponse { found: boolean; value?: string | null; version?: number | null; expiresAtUtc?: string | null }
interface GetManyResponse { values: Array<ValueResponse & { key: string }> }
interface ConditionalResponse { applied: boolean; version?: number | null; versionText?: string | null }
interface ExchangeResponse { previous: ValueResponse; mutationVersion?: number | null; previousVersionText?: string | null; mutationVersionText?: string | null }
interface HistoryEntry {
  action: string; status: string; database: string; target: string; connectionId: string; connectionName: string;
  command: string; summary: string; rowCount: number; recordsAffected: number; completeness: string;
}
interface BrowserRequest { method: string; path: string; body: Record<string, unknown>; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_KV_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_KV_REAL_BASE_URL is required; use the isolated real KV runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real KV Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB29 isolated evidence', username: 'wb29_admin', password,
    bearerToken: `wb29_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb29_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  writer = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(writer).toMatchObject({ username, isSuperuser: false });
  expect(writer.token).toBeTruthy();
  expect(writer.tokenId).toBeTruthy();
  // One bounded seed batch; the shared runner owns Server/content cleanup.
  const entries = Array.from({ length: seedCount }, (_, position) => ({ key: seedKey(position), value: base64(seedValue(position)) }));
  const inserted = await apiJson<{ versions: Record<string, number> }>(request, 'POST', `${kvPath}/set-many`, { entries, expiresAtUtc: null }, 200, administrator.token);
  seedVersions = inserted.versions;
  expect(Object.keys(seedVersions).sort()).toEqual(entries.map((entry) => entry.key).sort());
  expect(Object.values(seedVersions).every((version) => Number.isSafeInteger(version) && version > 0)).toBe(true);
  const listed = await apiJson<{ keyspaces: string[] }>(request, 'POST', `/v1/db/${database}/kv/keyspaces`, undefined, 200, administrator.token);
  expect(listed.keyspaces).toEqual([keyspace]);
});

test.beforeEach(async ({ request }) => {
  // An earlier failed revoke assertion cannot poison another independent test.
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false });
  }
});

test('real Scan100 and opaque-cursor tail51 preserve Get values/versions and export only the currently loaded keys', async ({ page }) => {
  const { evidence, initial } = await openKv(page);
  assertSeedScan(initial, 0, 100);
  expect(initial.hasMore).toBe(true);
  expect(typeof initial.nextCursor === 'string' && initial.nextCursor.length > 0).toBe(true);
  await expect(surface(page).locator('.kv-pager')).toContainText('Loaded 100 keys. Cursor is ready for the next page.');
  await expect(surface(page).locator('.kv-value-preview')).toContainText(seedValue(0));
  await expect(surface(page).locator('.kv-detail-strip')).toContainText(`version ${seedVersions[seedKey(0)]}`);
  const initialExport = await roundTripExport(page);
  assertExport(initialExport, initial.entries);
  expect(initialExport).toHaveLength(100);
  expect(initialExport.length).toBeLessThan(seedCount);

  await tab(page, '批量操作');
  const requestedKeys = [seedKey(0), seedKey(150)];
  await surface(page).getByPlaceholder('Keys, one per line', { exact: true }).fill(requestedKeys.join('\n'));
  const got = await perform(page, 'get-many', () => surface(page).getByRole('button', { name: 'Batch get', exact: true }).click());
  expect(got.status()).toBe(200);
  expect(got.request().postDataJSON()).toEqual({ keys: requestedKeys });
  const values = await got.json() as GetManyResponse;
  expect(values.values).toHaveLength(2);
  expect(values.values).toEqual(requestedKeys.map((key) => expect.objectContaining({ key, found: true,
    value: base64(seedValue(Number(key.slice(-4)))), version: seedVersions[key] })));
  const getHistory = await latestHistory(page, 'batch get');
  assertHistory(getHistory, 'success');
  expect(getHistory).toMatchObject({ rowCount: 2, completeness: 'complete' });
  const panel = await openResult(page);
  await panel.locator('.n-tabs-tab[data-name="raw"]').click();
  await expect(panel.locator('.workbench-result-panel__result')).toContainText(seedValue(0));
  await expect(panel.locator('.workbench-result-panel__result')).toContainText(seedValue(150));
  await panel.getByTitle('关闭结果', { exact: true }).click();

  await tab(page, '浏览器');
  const tailResponse = await perform(page, 'scan', () => surface(page).getByRole('button', { name: 'Load more', exact: true }).click());
  expect(tailResponse.status()).toBe(200);
  expect(tailResponse.request().postDataJSON()).toEqual({ prefix: '', cursor: initial.nextCursor, limit: 100 });
  const tail = await tailResponse.json() as ScanResponse;
  assertSeedScan(tail, 100, 51);
  expect(tail.hasMore).toBe(false);
  expect(tail.nextCursor ?? null).toBeNull();
  await expect(surface(page).locator('.kv-pager')).toContainText('Loaded 151 keys. End of current scan window.');
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  await expect(surface(page).getByRole('button', { name: 'Load more', exact: true })).toBeDisabled();
  const exported = await roundTripExport(page);
  assertExport(exported, [...initial.entries, ...tail.entries]);
  const history = await latestHistory(page, 'scan');
  assertHistory(history, 'success');
  expect(history).toMatchObject({ rowCount: seedCount, completeness: 'complete' });
  expect(history?.command).toContain(keyspace);
  expect(new URL(page.url()).searchParams.get('node')).toBe(keyspace);
  expect(writes(evidence)).toHaveLength(0);
  assertEvidence(evidence);
  await persistEvidence('real-kv-cursor-get-export', { requests: evidence.requests, initial, initialExport, values, getHistory,
    tail, exported, history, loadedKeys: seedCount,
    scope: 'Current loaded windows only; this does not assert a keyspace snapshot or Server resource budgets.' });
});

test('normal WRITE approvals consume NX success, NX not-applied and exchange once with actual version text and administrator Get', async ({ page, request }) => {
  const { evidence } = await openKv(page);
  await tab(page, '批量操作');
  await choose(page, 'Set condition', 'Only if absent (NX)');
  await fillEditor(page, writeKey, firstWrite);
  const created = await approve(page, evidence, 'Stage set', 'set-conditional', writeKey);
  const applied = created.terminal as ConditionalResponse;
  expect(applied.applied).toBe(true);
  assertExactVersion(applied.versionText, applied.version);
  expect(created.body).toEqual({ key: writeKey, value: base64(firstWrite), expiresAtUtc: null, condition: 1 });
  const createdHistory = await latestHistory(page, 'Set');
  assertHistory(createdHistory, 'success');
  expect(createdHistory?.recordsAffected).toBe(1);
  expect(createdHistory?.command).toContain(`"${writeKey}"`);
  expect(createdHistory?.command).toContain(' NX ');
  expect(createdHistory?.summary).toContain(`version=${applied.versionText}`);
  const afterCreate = await administratorGet(request, writeKey);
  expect(afterCreate).toMatchObject({ found: true, value: base64(firstWrite), version: applied.version });

  // Successful Schema refresh clears transient input, but does not replay its
  // consumed approval. Every next write is staged through the visible editor.
  await choose(page, 'Set condition', 'Only if absent (NX)');
  await fillEditor(page, writeKey, ignoredWrite);
  const ignored = await approve(page, evidence, 'Stage set', 'set-conditional', writeKey);
  const notApplied = ignored.terminal as ConditionalResponse;
  expect(notApplied.applied).toBe(false);
  if (notApplied.versionText != null) assertExactVersion(notApplied.versionText, notApplied.version);
  expect(ignored.body).toEqual({ key: writeKey, value: base64(ignoredWrite), expiresAtUtc: null, condition: 1 });
  const ignoredHistory = await latestHistory(page, 'Set');
  assertHistory(ignoredHistory, 'success');
  expect(ignoredHistory?.recordsAffected).toBe(0);
  expect(ignoredHistory?.summary).toContain('affected 0');
  expect(ignoredHistory?.command).toContain(`"${writeKey}"`);
  const afterIgnored = await administratorGet(request, writeKey);
  expect(afterIgnored).toEqual(afterCreate);

  await choose(page, 'KV operation', 'Get and set');
  await fillEditor(page, writeKey, exchangedWrite);
  const exchanged = await approve(page, evidence, 'Stage exchange', 'get-and-set', writeKey);
  const terminal = exchanged.terminal as ExchangeResponse;
  expect(terminal.previous).toMatchObject({ found: true, value: base64(firstWrite), version: applied.version });
  assertExactVersion(terminal.previousVersionText, terminal.previous.version);
  assertExactVersion(terminal.mutationVersionText, terminal.mutationVersion);
  expect(BigInt(terminal.mutationVersionText!)).toBeGreaterThan(BigInt(applied.versionText!));
  expect(exchanged.body).toEqual({ key: writeKey, value: base64(exchangedWrite), expiresAtUtc: null });
  const exchangeHistory = await latestHistory(page, 'Get and set');
  assertHistory(exchangeHistory, 'success');
  expect(exchangeHistory?.recordsAffected).toBe(1);
  expect(exchangeHistory?.command).toContain(`"${writeKey}"`);
  expect(exchangeHistory?.summary).toContain(`previousVersion=${terminal.previousVersionText}`);
  expect(exchangeHistory?.summary).toContain(`mutationVersion=${terminal.mutationVersionText}`);
  const afterExchange = await administratorGet(request, writeKey);
  expect(afterExchange).toMatchObject({ found: true, value: base64(exchangedWrite), version: terminal.mutationVersion });
  expect(writes(evidence)).toHaveLength(3);
  expect(writes(evidence).map((entry) => entry.path)).toEqual([`${kvPath}/set-conditional`, `${kvPath}/set-conditional`, `${kvPath}/get-and-set`]);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  assertEvidence(evidence);
  await persistEvidence('real-kv-approved-atomic-subset', { requests: evidence.requests, created, createdHistory, afterCreate,
    ignored, ignoredHistory, afterIgnored, exchanged, exchangeHistory, afterExchange,
    databaseGrant: 'WRITE', isSuperuser: false, approvalsConsumed: 3, writeRequests: 3,
    scope: 'Three atomic terminals only; complete atomic, TTL, CAS and Int64 precision matrices are separate.' });
});

test('real REVOKE rejects a normally imported approval with HTTP403 and same-token READ/Schema refresh cannot refill or replay it', async ({ page, request }) => {
  const { evidence, initial } = await openKv(page);
  // The approved atomic key sorts before Seed after the preceding serial
  // journey; select the original seed explicitly through the visible grid.
  await surface(page).locator('.kv-key-button').filter({ hasText: /^Seed:0000$/u }).click();
  await expect(surface(page).locator('.kv-value-preview')).toContainText(seedValue(0));
  const panel = await openResult(page);
  await panel.locator('.n-tabs-tab[data-name="raw"]').click();
  await expect(panel.locator('.workbench-result-panel__result')).toContainText(seedValue(0));
  await panel.getByTitle('关闭结果', { exact: true }).click();
  await tab(page, '统计');
  await expect(surface(page).locator('.kv-stats')).toContainText('Active');
  await expect(surface(page).locator('.kv-stats')).toContainText('152');
  await tab(page, '批量操作');
  await fillEditor(page, deniedKey, editorDraft);
  await surface(page).getByPlaceholder('Keys, one per line', { exact: true }).fill(deniedKey);
  await surface(page).getByPlaceholder('Batch set, one key=value per line', { exact: true }).fill(`${deniedKey}=${editorDraft}`);
  const imported = { format: 'sonnetdb-kv-v1', keyspace, key: deniedKey, valueBase64: base64(importDraft), expiresAtUtc: null };
  // Buffer-backed setInputFiles uses the real ordinary file-selection handler,
  // without creating a filesystem temp or injecting private component state.
  await surface(page).locator('.kv-file-input').setInputFiles({ name: 'wb29-rejected.kv.jsonl', mimeType: 'application/x-ndjson',
    buffer: Buffer.from(`${JSON.stringify(imported)}\n`, 'utf8') });
  const approval = page.getByRole('dialog', { name: 'KV operation batch' });
  await expect(approval).toBeVisible();
  await expect(approval).toContainText(`${database}.${keyspace}`);
  await expect(approval).toContainText('wb29-rejected.kv.jsonl');
  await expect(surface(page).locator('.kv-file-input')).toHaveValue('');
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, 'set-many', () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(denied.status()).toBe(403);
  const rejection = await denied.json() as { code?: string; error?: string };
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  await hiddenPermissionPayload(page);
  const deniedHistory = await latestHistory(page, 'Set');
  assertHistory(deniedHistory, 'error');
  expect(deniedHistory?.recordsAffected).toBe(0);
  expect(writes(evidence)).toHaveLength(1);
  expect(writes(evidence)[0]).toMatchObject({ usedSession: true,
    body: { entries: [{ key: deniedKey, value: base64(importDraft) }], expiresAtUtc: null } });
  const absent = await administratorGet(request, deniedKey);
  expect(absent.found).toBe(false);

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequests = evidence.requests.length;
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await page.getByTitle('刷新资源', { exact: true }).click();
  expect((await schema).status()).toBe(200);
  await expect(page.getByTitle('刷新资源', { exact: true }).locator('svg')).not.toHaveClass(/is-spinning/u);
  await hiddenPermissionPayload(page);
  await tab(page, '统计');
  await hiddenPermissionPayload(page);
  await tab(page, '浏览器');
  await hiddenPermissionPayload(page);
  await tab(page, '批量操作');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: 'Scan', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '导出 round-trip', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '导入文件', exact: true })).toBeDisabled();
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const sameSession = await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, writer.token);
  expect(sameSession).toBe(true);
  const stillAbsent = await administratorGet(request, deniedKey);
  expect(stillAbsent.found).toBe(false);
  assertEvidence(evidence);
  await persistEvidence('real-kv-revoked-import-approval', { requests: evidence.requests, initial, imported,
    deniedStatus: denied.status(), rejection, deniedHistory, absent, stillAbsent, databaseGrant: 'WRITE', isSuperuser: false,
    regrant: 'READ', schemaStatus: 200, sameSession, stillLocked: true, approvalConsumed: true,
    valueStatsCursorResultEditorImportSurfacesCleared: true, writeRequests: writes(evidence).length,
    scope: 'Explicit safe recovery is a separate slice; READ/Schema refresh cannot unlock or replay the old import approval.' });
});

function surface(page: Page) { return page.getByTestId('workbench-kv'); }
function resultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({ has: page.locator('.workbench-result-panel__title').filter({ hasText: /^KV operation result$/u }) });
}
function seedKey(position: number) { return `Seed:${String(position).padStart(4, '0')}`; }
function seedValue(position: number) { return `WB29:Seed:OriginalPayload:${String(position).padStart(4, '0')}`; }
function base64(value: string) { return Buffer.from(value, 'utf8').toString('base64'); }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => !['scan', 'stats', 'get-many'].includes(entry.path.slice(kvPath.length + 1))); }
function matches(action: string) {
  return (response: Response) => response.request().method() === 'POST' && decodeURIComponent(new URL(response.url()).pathname) === `${kvPath}/${action}`;
}
async function perform(page: Page, action: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(action), { timeout: responseTimeout });
  await start();
  return response;
}
async function tab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function fillEditor(page: Page, key: string, value: string): Promise<void> {
  await surface(page).getByPlaceholder('Key', { exact: true }).fill(key);
  await surface(page).getByPlaceholder('Value', { exact: true }).fill(value);
}
async function choose(page: Page, label: string, option: string): Promise<void> {
  await surface(page).locator(`[aria-label="${label}"]`).click();
  await page.locator('.n-base-select-option').getByText(option, { exact: true }).click();
}
async function openKv(page: Page): Promise<{ evidence: BrowserEvidence; initial: ScanResponse }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    // Metadata refresh is allowed while permission stays latched; only this
    // selected keyspace's actual read/write requests are counted here.
    if (request.method() !== 'POST' || !path.startsWith(`${kvPath}/`)) return;
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
  const scan = page.waitForResponse(matches('scan'), { timeout: responseTimeout });
  const query = new URLSearchParams({ tool: 'kv', database, model: 'kv', node: keyspace });
  await page.goto(`/admin/app/sql?${query}`);
  const response = await scan;
  expect(response.status()).toBe(200);
  expect(response.request().postDataJSON()).toEqual({ prefix: '', cursor: null, limit: 100 });
  const initial = await response.json() as ScanResponse;
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `kv:${keyspace}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  await expect(surface(page).locator('.kv-toolbar__title')).toHaveText(keyspace);
  await expect(surface(page).locator('.kv-toolbar__meta')).toContainText(`${database} · 100 loaded keys`);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).not.toHaveClass(/n-button--loading/u);
  return { evidence, initial };
}
function assertSeedScan(scan: ScanResponse, offset: number, count: number): void {
  expect(scan.entries).toHaveLength(count);
  expect(scan.entries.map((entry) => entry.key)).toEqual(Array.from({ length: count }, (_, position) => seedKey(offset + position)));
  expect(scan.entries.every((entry) => entry.value === base64(seedValue(Number(entry.key.slice(-4))))
    && entry.version === seedVersions[entry.key] && (entry.expiresAtUtc ?? null) === null)).toBe(true);
}
function assertExport(exported: Array<Record<string, unknown>>, entries: KvEntry[]): void {
  expect(exported).toHaveLength(entries.length);
  expect(exported.map((row) => ({ format: row.format, keyspace: row.keyspace, key: row.key, valueBase64: row.valueBase64, expiresAtUtc: row.expiresAtUtc })))
    .toEqual(entries.map((entry) => ({ format: 'sonnetdb-kv-v1', keyspace, key: entry.key, valueBase64: entry.value, expiresAtUtc: entry.expiresAtUtc ?? null })));
  expect(exported.every((row) => Buffer.from(String(row.valueBase64), 'base64').toString('utf8') === seedValue(Number(String(row.key).slice(-4))))).toBe(true);
}
function assertExactVersion(text: string | null | undefined, number: number | null | undefined): void {
  expect(text).toMatch(/^[1-9]\d*$/u);
  expect(Number.isSafeInteger(number) && number! > 0).toBe(true);
  expect(text).toBe(String(number));
}
function assertHistory(history: HistoryEntry | undefined, status: string): void {
  expect(history).toMatchObject({ status, database, target: keyspace, connectionId: profileId, connectionName: profileName });
}
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
}
async function approve(page: Page, evidence: BrowserEvidence, stageLabel: string, action: string, key: string): Promise<{ status: number; body: unknown; terminal: ConditionalResponse | ExchangeResponse }> {
  const before = writes(evidence).length;
  await surface(page).getByRole('button', { name: stageLabel, exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'KV operation batch' });
  await expect(approval).toBeVisible();
  await expect(approval).toContainText(`${database}.${keyspace}`);
  await expect(approval).toContainText(key);
  expect(writes(evidence)).toHaveLength(before);
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  const response = await perform(page, action, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(response.status()).toBe(200);
  expect(response.headers()['x-sonnetdb-contract-version']).toBe('1');
  expect(response.headers()['x-request-id']).toBeTruthy();
  const terminal = await response.json() as ConditionalResponse | ExchangeResponse;
  expect((await schema).status()).toBe(200);
  await expect(approval).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).not.toHaveClass(/n-button--loading/u);
  expect(writes(evidence)).toHaveLength(before + 1);
  return { status: response.status(), body: response.request().postDataJSON(), terminal };
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `kv:${keyspace}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('kv-permission-lock')).toBeVisible();
  await expect(surface(page).locator('.kv-key-button')).toHaveCount(0);
  await expect(surface(page).locator('.kv-value-preview')).toHaveCount(0);
  await expect(surface(page).locator('.kv-detail-strip')).toHaveCount(0);
  await expect(surface(page).locator('.kv-pager')).toHaveCount(0);
  await expect(surface(page).locator('.kv-editor')).toHaveCount(0);
  await expect(surface(page).locator('.kv-batch')).toHaveCount(0);
  await expect(surface(page).locator('.kv-stats')).toHaveCount(0);
  await expect(surface(page).locator('.kv-file-input')).toHaveValue('');
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seedValue(0));
  await expect(surface(page)).not.toContainText(editorDraft);
  await expect(surface(page)).not.toContainText(importDraft);
  await expect(surface(page).getByTestId('kv-preview-budget')).toContainText('0/1000 keys');
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
async function openResult(page: Page) {
  await surface(page).getByRole('button', { name: '查看 KV 结果', exact: true }).click();
  await expect(resultPanel(page)).toBeVisible();
  return resultPanel(page);
}
async function roundTripExport(page: Page): Promise<Array<Record<string, unknown>>> {
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await surface(page).getByRole('button', { name: '导出 round-trip', exact: true }).click();
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    const content = await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
    const lines = content.trim().split(/\r?\n/u);
    if (lines.length > seedCount) throw new Error('KV preview export exceeded 151 rows.');
    return lines.map((line) => JSON.parse(line) as Record<string, unknown>);
  } finally { await download.delete(); }
}
async function administratorGet(request: APIRequestContext, key: string): Promise<ValueResponse> {
  return apiJson<ValueResponse>(request, 'POST', `${kvPath}/get`, { key }, 200, administrator.token);
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    return await response.json() as T;
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> {
  const response = await request.post(new URL('/v1/sql', serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows: 200 }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), 'The real administrator SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 262_144) throw new Error('Setup SQL exceeded 256 KiB.');
    const lines = content.trim().split(/\r?\n/u);
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
  const configured = process.env.SONNETDB_KV_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_KV_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const parent = await realpath(resolve(process.cwd(), '..', 'artifacts', 'wb29-validation-20261006'));
  if (!samePath(dirname(root), parent) || !/^kv-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be a kv-real run immediately inside artifacts/wb29-validation-20261006.');
  }
  const runInfo = await readFile(join(root, 'run.json'), { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  if (Buffer.byteLength(runInfo, 'utf8') > 65_536) throw new Error('Runner marker exceeded 64 KiB.');
  const marker = JSON.parse(runInfo) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'kv-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('KV evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, keyspace, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('KV evidence exceeded 1 MiB/file or 8 MiB/run.');
  if ([password, administrator?.token, writer?.token, administrator?.tokenId, writer?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) {
    throw new Error('Credential material must never be written to KV evidence.');
  }
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(content).digest('hex') });
}
