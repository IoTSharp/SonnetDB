import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Routed Vue -> real Vite proxy -> isolated Kestrel. API login installs a real
// non-superuser WRITE session. These current SQL windows and a two-point import
// do not cover login UI, readonly host props, a complete snapshot, Server scan /
// materialization / transfer / byte / heap budgets, the complete write-terminal
// matrix, explicit permission recovery, Studio, VS Code, AOT or release gates.
const database = 'wb31';
const measurement = 'DeviceMeasures_Original';
const columnNames = ['time', 'DeviceID', 'Payload_Original', 'Temperature_Original'];
const username = 'wb31_writer';
const password = 'Workbench31:OnlyLocal!';
const profileId = 'wb31-real';
const profileName = 'WB31 real local Server';
const seedCount = 501;
const seedStart = 1_780_000_000_000;
const seedTag = 'SeedDevice_Original';
const approvedTag = 'ApprovedDevice_Original';
const deniedTag = 'RejectedDevice_Original';
const editorDraft = 'WB31EditorDraftMustDisappear';
const deniedPayload = 'WB31RejectedImportMustDisappear';
const approvedRows = [
  [seedStart + 1_000_000, approvedTag, 'WB31ApprovedPayload_First', 42.5],
  [seedStart + 1_001_000, approvedTag, 'WB31ApprovedPayload_Second', 43.75],
];
const deniedRows = [[seedStart + 2_000_000, deniedTag, deniedPayload, 99.25]];
const sqlPath = `/v1/db/${database}/sql`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let writer: AuthIdentity;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface SqlParameter { kind: number; integerValue?: number; doubleValue?: number; stringValue?: string }
interface SqlStatement { sql: string; parameters?: Record<string, SqlParameter>; previewMaxRows?: number }
interface SqlBody extends Partial<SqlStatement> { statements?: SqlStatement[] }
interface EndFrame { type: 'end'; rowCount: number; recordsAffected: number; elapsedMs?: number; elapsedMilliseconds?: number; truncated?: boolean }
interface SqlFrames { raw: string; columns: string[]; rows: unknown[][]; ends: EndFrame[] }
interface BrowserRequest { path: string; body: SqlBody; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }
interface HistoryEntry {
  action: string; status: string; database: string; target: string; connectionId: string; connectionName: string;
  command: string; rowCount: number; recordsAffected: number; completeness: string;
}
interface MeasurementSchema { name: string; columns: Array<{ name: string; role: string; dataType: string }> }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_MEASUREMENT_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_MEASUREMENT_REAL_BASE_URL is required; use the isolated real Measurement runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Measurement Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB31 isolated evidence', username: 'wb31_admin', password,
    bearerToken: `wb31_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb31_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  writer = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(writer).toMatchObject({ username, isSuperuser: false });
  expect(writer.token).toBeTruthy();
  expect(writer.tokenId).toBeTruthy();
  await adminSql(request, `CREATE MEASUREMENT ${measurement} (DeviceID TAG, Payload_Original FIELD STRING, Temperature_Original FIELD FLOAT)`);
  // One bounded INSERT of 501 points, with no Measurement transaction wrapper.
  const values = Array.from({ length: seedCount }, (_, index) => `(${seedTime(index)}, '${seedTag}', '${seedPayload(index)}', ${seedTemperature(index)})`);
  const inserted = await adminSql(request, `INSERT INTO ${measurement} (${columnNames.join(', ')}) VALUES ${values.join(', ')}`);
  expect(inserted.ends).toHaveLength(1);
  expect(inserted.ends[0]).toMatchObject({ rowCount: 0, recordsAffected: seedCount });
  const seeded = await adminSql(request, `SELECT ${columnNames.join(', ')} FROM ${measurement} ORDER BY time DESC LIMIT 502`, 502);
  expect(seeded.columns).toEqual(columnNames);
  expect(seeded.rows).toEqual(Array.from({ length: seedCount }, (_, position) => seedRow(seedCount - 1 - position)));
  const schema = await apiJson<{ measurements: MeasurementSchema[] }>(request, 'GET', `/v1/db/${database}/schema`, undefined, 200, administrator.token);
  expect(schema.measurements).toHaveLength(1);
  expect(schema.measurements[0].name).toBe(measurement);
  expect(schema.measurements[0].columns.filter((column) => column.name !== 'time').map((column) => column.name)).toEqual(columnNames.slice(1));
  expect(schema.measurements[0].columns.find((column) => column.name === 'DeviceID')?.role.toLowerCase()).toBe('tag');
  expect(schema.measurements[0].columns.filter((column) => column.role.toLowerCase() === 'field').map((column) => column.name)).toEqual(columnNames.slice(2));
});

test.beforeEach(async ({ request }) => {
  // A prior actual REVOKE cannot poison the next independent browser page.
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false });
  }
});

test('real MixedCase Measurement reads 100 and 500 windows with time/TAG parameters and current JSON/CSV exports', async ({ page }) => {
  const { evidence, initial } = await openMeasurement(page);
  assertSeedWindow(initial, 100, 500);
  await expect(surface(page)).toHaveAttribute('data-state', 'longContent');
  await expect(surface(page).locator('.measurement-grid')).toContainText(seedPayload(500));
  await expect(surface(page).locator('.measurement-time').first()).toHaveAttribute('title', String(seedTime(500)));
  await expect(surface(page).locator('.measurement-column-title strong')).toHaveText(columnNames);
  const initialJson = await exportText(page, 'JSON');
  const initialCsv = await exportText(page, 'CSV');
  assertExports(initialJson, initialCsv, initial.rows);
  const initialHistory = await latestHistory(page, 'points');
  assertReadHistory(initialHistory, initial, 'points');

  await surface(page).locator('.measurement-filterbar__limit .n-select').click();
  await page.locator('.n-base-select-option').getByText('500 行', { exact: true }).click();
  const resized = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?查询$/u }).click());
  const large = await actualSql(resized);
  await settledPoints(page, 500);
  assertSeedWindow(large, 500, 500);
  expect(resized.request().postDataJSON()).toEqual({ sql: pointSql(), parameters: { limit: { kind: 2, integerValue: 500 } }, previewMaxRows: 500 });
  const largeJson = await exportText(page, 'JSON');
  const largeCsv = await exportText(page, 'CSV');
  assertExports(largeJson, largeCsv, large.rows);
  expect(JSON.parse(largeJson)).toHaveLength(500);
  expect(large.rows.length).toBeLessThan(seedCount);
  const largeHistory = await latestHistory(page, 'points');
  assertReadHistory(largeHistory, large, 'points');

  // Use the native input's default minute precision and canonical value. Zero
  // seconds are omitted by Chromium; Playwright fill checks the exact value.
  // Convert in the browser's local timezone, matching datetime-local semantics.
  const bounds = await page.evaluate(({ from, to }) => {
    const local = (value: number) => {
      const date = new Date(value);
      const pad = (number: number) => String(number).padStart(2, '0');
      if (date.getSeconds() !== 0 || date.getMilliseconds() !== 0) throw new Error('The real time-filter boundaries must be minute aligned.');
      return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
    };
    return { from: local(from), to: local(to) };
  }, { from: seedTime(200), to: seedTime(260) });
  const times = surface(page).locator('.measurement-filterbar input[type="datetime-local"]');
  await times.nth(0).fill(bounds.from);
  await times.nth(1).fill(bounds.to);
  await surface(page).locator('.measurement-filterbar label').nth(2).locator('.n-select').click();
  await page.locator('.n-base-select-option').getByText('DeviceID', { exact: true }).click();
  await surface(page).getByPlaceholder('精确匹配', { exact: true }).fill(seedTag);
  const filteredResponse = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?查询$/u }).click());
  const filtered = await actualSql(filteredResponse);
  await settledPoints(page, 61);
  assertSeedWindow(filtered, 61, 260);
  expect(filteredResponse.request().postDataJSON()).toEqual({ sql: pointSql('time >= @from AND time <= @to AND DeviceID = @tag'),
    parameters: { limit: { kind: 2, integerValue: 500 }, from: { kind: 2, integerValue: seedTime(200) },
      to: { kind: 2, integerValue: seedTime(260) }, tag: { kind: 1, stringValue: seedTag } }, previewMaxRows: 500 });
  const filteredJson = await exportText(page, 'JSON');
  const filteredCsv = await exportText(page, 'CSV');
  assertExports(filteredJson, filteredCsv, filtered.rows);
  const filteredHistory = await latestHistory(page, 'points');
  assertReadHistory(filteredHistory, filtered, 'points');
  expect(new URL(page.url()).searchParams.get('node')).toBe(measurement);
  expect(writes(evidence)).toHaveLength(0);
  assertEvidence(evidence);
  await persistEvidence('real-measurement-current-windows', { requests: evidence.requests, initial, initialJson, initialCsv, initialHistory,
    large, largeJson, largeCsv, largeHistory, filtered, filteredJson, filteredCsv, filteredHistory, seededPoints: seedCount,
    scope: '100/500 and filtered SQL windows, current JSON/CSV only; not a complete Measurement snapshot or Server resource budget.' });
});

test('normal non-superuser WRITE file import consumes one approval with two complete INSERT ends and administrator values/history', async ({ page, request }) => {
  const { evidence } = await openMeasurement(page);
  const approval = await stageFileImport(page, 'wb31-approved.csv', approvedRows);
  expect(writes(evidence)).toHaveLength(0);
  const refreshed = page.waitForResponse(matches(sqlPath), { timeout: responseTimeout });
  const response = await perform(page, `${sqlPath}/batch`, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  const actual = await actualSql(response);
  assertImportBatch(response.request().postDataJSON() as SqlBody, approvedRows);
  expect(actual.rows).toEqual([]);
  expect(actual.ends).toHaveLength(approvedRows.length);
  expect(actual.ends.map((frame) => frame.rowCount)).toEqual([0, 0]);
  expect(actual.ends.map((frame) => frame.recordsAffected)).toEqual([1, 1]);
  await expect(approval).toHaveCount(0);
  const postWriteRead = await actualSql(await refreshed);
  expect(postWriteRead.columns).toEqual(columnNames);
  const history = await latestHistory(page, 'import');
  assertHistory(history, 'success');
  expect(history).toMatchObject({ recordsAffected: 2, completeness: 'complete' });
  expect(history?.command).toContain(`INSERT INTO ${measurement}`);
  expect(history?.command).not.toMatch(/\bBEGIN\b|\bCOMMIT\b/iu);
  const stored = await administratorRows(request, approvedTag);
  expect(stored.columns).toEqual(columnNames);
  expect(stored.rows).toEqual(approvedRows);
  expect(writes(evidence)).toHaveLength(1);
  await tab(page, '数据点');
  await expect(surface(page).getByRole('button', { name: '新增数据点', exact: true })).toBeEnabled();
  await expect(surface(page).locator('.measurement-grid')).toContainText(String(approvedRows[1][2]));
  const nextRead = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?刷新$/u }).click());
  await actualSql(nextRead);
  await settledPoints(page, 100);
  expect(writes(evidence)).toHaveLength(1);
  await expect(approval).toHaveCount(0);
  assertEvidence(evidence);
  await persistEvidence('real-measurement-approved-import', { requests: evidence.requests, actual, stored, history, postWriteRead,
    approvedRows, databaseGrant: 'WRITE', isSuperuser: false, approvalsConsumed: 1, writeRequests: 1,
    scope: 'Two complete per-point INSERT terminals with independent administrator SELECT; complete write/Int64 matrices remain separate.' });
});

test('real REVOKE rejects an old normal file approval with 403 and READ/same-token Schema200 cannot refill or replay cleared payloads', async ({ page, request }) => {
  const { evidence, initial } = await openMeasurement(page);
  await tab(page, 'Schema');
  await expect(surface(page).locator('.measurement-schema')).toContainText('Payload_Original');
  await expect(surface(page).locator('.measurement-schema .n-data-table-td')).not.toHaveCount(0);
  await tab(page, '实时监控');
  const monitorResponse = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u }).click());
  const monitored = await actualSql(monitorResponse);
  expect(monitored.columns).toEqual(columnNames);
  expect(monitored.rows).toHaveLength(100);
  await settledMonitor(page, 100);
  await tab(page, '数据点');
  await surface(page).getByRole('button', { name: '新增数据点', exact: true }).click();
  await surface(page).locator('.point-field').filter({ hasText: 'Payload_Original' }).locator('input').fill(editorDraft);
  await expect(surface(page).locator('.point-editor')).toContainText('新增数据点');
  const approval = await stageFileImport(page, 'wb31-rejected.csv', deniedRows);
  await expect(approval).toContainText(`${database}.${measurement}`);
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, `${sqlPath}/batch`, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  const rejection = await actualPermissionFailure(denied);
  expect(denied.status()).toBe(403);
  assertImportBatch(denied.request().postDataJSON() as SqlBody, deniedRows);
  await hiddenPermissionPayload(page);
  const history = await latestHistory(page, 'import');
  assertHistory(history, 'error');
  expect(history).toMatchObject({ recordsAffected: 0, completeness: 'partial' });
  const absent = await administratorRows(request, deniedTag);
  expect(absent.rows).toEqual([]);
  expect(writes(evidence)).toHaveLength(1);

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequests = evidence.requests.length;
  const schemaStatus = await refreshRealSchema(page);
  await assertAllLockedViews(page);
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const sameSession = await hasSameSession(page);
  expect(sameSession).toBe(true);
  const stillAbsent = await administratorRows(request, deniedTag);
  expect(stillAbsent.rows).toEqual([]);
  assertEvidence(evidence);
  await persistEvidence('real-measurement-revoked-import', { requests: evidence.requests, initial, monitored, deniedRows,
    deniedStatus: denied.status(), rejection, history, absent, stillAbsent, databaseGrant: 'WRITE', isSuperuser: false,
    regrant: 'READ', schemaStatus, sameSession, stillLocked: true, approvalConsumed: true,
    pointMonitorSchemaEditorImportApprovalCleared: true, writeRequests: 1,
    scope: 'Actual revoke and same-token Schema refresh; explicit permission recovery remains a separate slice.' });
});

test('independent real monitor403 clears its previous result and locks every Measurement view without later model requests', async ({ page, request }) => {
  const { evidence, initial } = await openMeasurement(page);
  await tab(page, '实时监控');
  const first = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u }).click());
  const monitored = await actualSql(first);
  expect(monitored.columns).toEqual(columnNames);
  expect(monitored.rows).toHaveLength(100);
  expect(first.request().postDataJSON()).toEqual({ sql: `SELECT * FROM ${measurement} ORDER BY time DESC LIMIT 100;`, previewMaxRows: 100 });
  await settledMonitor(page, 100);
  const history = await latestHistory(page, 'monitor');
  assertReadHistory(history, monitored, 'monitor');
  await expect(surface(page).locator('.monitor-grid-panel')).toContainText(String(approvedRows[1][2]));
  await expect(surface(page).locator('.monitor-stats > div').nth(1).locator('strong')).not.toHaveText('—');
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const denied = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u }).click());
  const rejection = await actualPermissionFailure(denied);
  expect(denied.status()).toBe(403);
  await hiddenPermissionPayload(page);
  const lockedRequests = evidence.requests.length;
  await assertAllLockedViews(page);
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(0);
  const sameSession = await hasSameSession(page);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-measurement-revoked-monitor', { requests: evidence.requests, initial, monitored, history,
    deniedStatus: denied.status(), rejection, sameSession, stillLocked: true, modelRequestsAfterLock: 0,
    pointMonitorSchemaEditorImportApprovalCleared: true,
    scope: 'One manually refreshed monitor receives real 403; auto-monitor timers and explicit recovery remain separately evidenced.' });
});

function surface(page: Page) { return page.getByTestId('workbench-measurement'); }
function seedTime(index: number) { return seedStart + index * 1_000; }
function seedPayload(index: number) { return `WB31SeedPayload_${String(index).padStart(4, '0')}`; }
function seedTemperature(index: number) { return 25 + index / 4; }
function seedRow(index: number) { return [seedTime(index), seedTag, seedPayload(index), seedTemperature(index)]; }
function pointSql(where = '') { return `SELECT ${columnNames.join(', ')}\nFROM ${measurement}\n${where ? `WHERE ${where}\n` : ''}ORDER BY time DESC\nLIMIT @limit;`; }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => entry.path === `${sqlPath}/batch`); }
function matches(path: string) {
  return (response: Response) => response.request().method() === 'POST' && decodeURIComponent(new URL(response.url()).pathname) === path;
}
async function perform(page: Page, path: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(path), { timeout: responseTimeout });
  await start();
  return response;
}
async function tab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function openMeasurement(page: Page): Promise<{ evidence: BrowserEvidence; initial: SqlFrames }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    if (request.method() !== 'POST' || ![sqlPath, `${sqlPath}/batch`].includes(path)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ path, body: request.postDataJSON() as SqlBody,
      usedSession: request.headers().authorization === `Bearer ${writer.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: writer, db: database, id: profileId, name: profileName });
  const received = page.waitForResponse(matches(sqlPath), { timeout: responseTimeout });
  const query = new URLSearchParams({ tool: 'measurement', database, model: 'measurement', node: measurement });
  await page.goto(`/admin/app/sql?${query}`);
  const response = await received;
  expect(response.request().postDataJSON()).toEqual({ sql: pointSql(), parameters: { limit: { kind: 2, integerValue: 100 } }, previewMaxRows: 100 });
  const initial = await actualSql(response);
  expect(initial.columns).toEqual(columnNames);
  expect(initial.rows).toHaveLength(100);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', measurement);
  await expect(surface(page).locator('.measurement-toolbar__title')).toHaveText(measurement);
  await settledPoints(page, 100);
  return { evidence, initial };
}
async function settledPoints(page: Page, count: number): Promise<void> {
  await expect(surface(page).getByRole('button', { name: /^(?:loading\s+)?查询$/u })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.measurement-statusbar')).toContainText(`${count} 个点`);
}
async function settledMonitor(page: Page, count: number): Promise<void> {
  await expect(surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.monitor-stats > div').nth(2).locator('strong')).toHaveText(String(count));
  await expect(surface(page).locator('.monitor-grid-panel .n-data-table-td')).not.toHaveCount(0);
}
function assertSeedWindow(result: SqlFrames, count: number, lastIndex: number): void {
  expect(result.columns).toEqual(columnNames);
  expect(result.rows).toEqual(Array.from({ length: count }, (_, position) => seedRow(lastIndex - position)));
  expect(result.ends).toHaveLength(1);
  expect(result.ends[0]).toMatchObject({ rowCount: count, recordsAffected: -1 });
}
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
}
function assertHistory(history: HistoryEntry | undefined, status: string): void {
  expect(history).toMatchObject({ status, database, target: measurement, connectionId: profileId, connectionName: profileName });
}
function assertReadHistory(history: HistoryEntry | undefined, result: SqlFrames, action: string): void {
  assertHistory(history, 'success');
  expect(history).toMatchObject({ action, rowCount: result.rows.length, recordsAffected: -1,
    completeness: result.ends[0]?.truncated === true ? 'truncated' : 'complete' });
  expect(history?.command).toContain(measurement);
}
function assertExports(json: string, csv: string, rows: unknown[][]): void {
  const objects = JSON.parse(json) as Array<Record<string, unknown>>;
  expect(objects).toEqual(rows.map((row) => Object.fromEntries(columnNames.map((column, index) => [column, row[index]]))));
  // This seed uses plain scalar cells without CSV metacharacters. Check the
  // downloaded bytes independently, without calling the production exporter.
  expect(csv.endsWith('\n')).toBe(true);
  const lines = csv.trimEnd().split(/\r?\n/u);
  expect(lines.length).toBeLessThanOrEqual(501);
  expect(lines[0].split(',')).toEqual(columnNames);
  expect(lines.slice(1).map((line) => line.split(','))).toEqual(rows.map((row) => row.map(String)));
}
async function exportText(page: Page, format: 'JSON' | 'CSV'): Promise<string> {
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await surface(page).getByRole('button', { name: `导出 ${format}`, exact: true }).click();
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    expect(download.suggestedFilename()).toBe(`${database}_${measurement}.${format.toLowerCase()}`);
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    return await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  } finally { await download.delete(); }
}
async function stageFileImport(page: Page, name: string, rows: unknown[][]) {
  await tab(page, '文件导入');
  const csv = `${columnNames.join(',')}\n${rows.map((row) => row.join(',')).join('\n')}\n`;
  // Ordinary file-selection handler, not a prop harness or private draft.
  await surface(page).locator('.measurement-file-input').setInputFiles({ name, mimeType: 'text/csv', buffer: Buffer.from(csv, 'utf8') });
  await expect(surface(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据', { exact: true })).toHaveValue(csv);
  await expect(surface(page).locator('.measurement-file-input')).toHaveValue('');
  await surface(page).getByRole('button', { name: '解析', exact: true }).click();
  await expect(surface(page).locator('.measurement-import-grid')).toContainText(`${rows.length} 行`);
  await expect(surface(page).locator('.measurement-import-preview')).toContainText(String(rows[0][2]));
  await surface(page).getByRole('button', { name: '暂存导入', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Measurement import' });
  await expect(approval).toBeVisible();
  await expect(approval).toContainText(`${database}.${measurement}`);
  await expect(approval).toContainText(`INSERT ${rows.length} POINTS INTO ${measurement}`);
  await expect(approval.locator('.write-approval-item')).toHaveCount(1);
  return approval;
}
function assertImportBatch(body: SqlBody, rows: unknown[][]): void {
  expect(body.statements).toHaveLength(rows.length);
  expect(body.statements?.map((statement) => statement.sql)).toEqual(rows.map(() =>
    `INSERT INTO ${measurement} (${columnNames.join(', ')}) VALUES (@point_0_time, @point_1_DeviceID, @point_2_Payload_Original, @point_3_Temperature_Original);`));
  expect(body.statements?.map((statement) => statement.parameters)).toEqual(rows.map((row) => ({
    point_0_time: { kind: 2, integerValue: row[0] }, point_1_DeviceID: { kind: 1, stringValue: row[1] },
    point_2_Payload_Original: { kind: 1, stringValue: row[2] }, point_3_Temperature_Original: { kind: 3, doubleValue: row[3] },
  })));
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', measurement);
  await expect(surface(page).getByTestId('measurement-permission-lock')).toBeVisible();
  await expect(surface(page).locator('.measurement-grid .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.monitor-grid-panel .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-schema .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.point-editor')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-import-grid')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-import-errors')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-approval-zone')).toBeEmpty();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seedPayload(500));
  await expect(surface(page)).not.toContainText(String(approvedRows[1][2]));
  await expect(surface(page)).not.toContainText(editorDraft);
  await expect(surface(page)).not.toContainText(deniedPayload);
  await expect(surface(page).locator('.measurement-statusbar code')).toHaveCount(0);
  await expect(surface(page).locator('.monitor-chart-panel canvas')).toHaveCount(0);
  const importText = surface(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据', { exact: true });
  if (await importText.count()) { await expect(importText).toHaveValue(''); await expect(importText).toBeDisabled(); }
  const monitor = surface(page).locator('.monitor-stats');
  if (await monitor.count()) {
    await expect(monitor.locator(':scope > div').nth(0).locator('strong')).toHaveText('PAUSED');
    await expect(monitor.locator(':scope > div').nth(1).locator('strong')).toHaveText('—');
    await expect(monitor.locator(':scope > div').nth(2).locator('strong')).toHaveText('0');
    await expect(monitor.locator(':scope > div').nth(3).locator('strong')).toHaveText('—');
    await expect(surface(page).locator('.monitor-chart-panel code')).toHaveText('');
  }
}
async function assertAllLockedViews(page: Page): Promise<void> {
  // Four fixed views, no retry loop or timer-based replay observation.
  await tab(page, 'Schema');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: '在 SQL 中查看', exact: true })).toBeDisabled();
  await tab(page, '实时监控');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '开始', exact: true })).toBeDisabled();
  await tab(page, '数据点');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: '查询', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '刷新', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '导出 JSON', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '导出 CSV', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '新增数据点', exact: true })).toBeDisabled();
  await tab(page, '文件导入');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: '选择文件', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '解析', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '暂存导入', exact: true })).toBeDisabled();
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
  return page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, writer.token);
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
async function actualSql(response: Response): Promise<SqlFrames> {
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('ndjson');
  return parseFrames(await response.text());
}
function boundedFrames(raw: string): unknown[] {
  if (Buffer.byteLength(raw, 'utf8') > 262_144) throw new Error('SQL evidence exceeded 256 KiB.');
  const lines = raw.trim().split(/\r?\n/u);
  if (lines.length > 1_024) throw new Error('SQL evidence exceeded 1024 NDJSON frames.');
  return lines.map((line) => JSON.parse(line) as unknown);
}
function isErrorFrame(frame: unknown): boolean {
  if (!frame || Array.isArray(frame) || typeof frame !== 'object') return false;
  const value = frame as { type?: string; error?: string; code?: string; message?: string };
  return value.type === 'error' || (typeof value.message === 'string' && (typeof value.error === 'string' || typeof value.code === 'string'));
}
function parseFrames(raw: string): SqlFrames {
  const frames = boundedFrames(raw);
  expect(frames.filter(isErrorFrame), 'HTTP200 is not sufficient if real SQL contains an error frame.').toEqual([]);
  const objects = frames.filter((frame) => frame && !Array.isArray(frame) && typeof frame === 'object') as Array<{ type?: string; columns?: string[] } & Partial<Omit<EndFrame, 'type'>>>;
  expect(objects.every((frame) => frame.type === 'meta' || frame.type === 'end')).toBe(true);
  expect(objects.at(-1)?.type).toBe('end');
  const columns = objects.find((frame) => frame.type === 'meta')?.columns ?? [];
  const rows = frames.filter(Array.isArray) as unknown[][];
  expect(rows.every((row) => row.length === columns.length)).toBe(true);
  const ends = objects.filter((frame) => frame.type === 'end') as EndFrame[];
  expect(ends.length).toBeGreaterThan(0);
  expect(ends.every((frame) => Number.isSafeInteger(frame.rowCount) && frame.rowCount >= 0
    && Number.isSafeInteger(frame.recordsAffected) && frame.recordsAffected >= -1
    && Number.isFinite(frame.elapsedMs ?? frame.elapsedMilliseconds)
    && (frame.elapsedMs ?? frame.elapsedMilliseconds ?? -1) >= 0)).toBe(true);
  expect(ends.reduce((count, frame) => count + frame.rowCount, 0)).toBe(rows.length);
  return { raw, columns, rows, ends };
}
async function actualPermissionFailure(response: Response): Promise<unknown> {
  if (response.status() === 403) {
    const rejection = await response.json() as { code?: string; error?: string };
    expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
    return rejection;
  }
  // An error after an earlier end can be carried by an HTTP200 NDJSON stream.
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('ndjson');
  const frames = boundedFrames(await response.text());
  const errors = frames.filter(isErrorFrame) as Array<{ code?: string; error?: string }>;
  expect(errors).toHaveLength(1);
  expect(errors[0].code ?? errors[0].error).toMatch(/forbidden|permission|access_denied/iu);
  return frames;
}
async function administratorRows(request: APIRequestContext, tag: string): Promise<SqlFrames> {
  return adminSql(request, `SELECT ${columnNames.join(', ')} FROM ${measurement} WHERE DeviceID = '${tag}' ORDER BY time ASC LIMIT 10`);
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    return await response.json() as T;
  } finally { await response.dispose(); }
}
async function adminSql(request: APIRequestContext, sql: string, previewMaxRows = 500, path = sqlPath): Promise<SqlFrames> {
  const response = await request.post(new URL(path, serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), 'The real administrator SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    return parseFrames(await response.text());
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> {
  await adminSql(request, sql, 500, '/v1/sql');
}
function samePath(left: string, right: string): boolean {
  return process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right;
}
async function initializeEvidenceRoot(): Promise<void> {
  const configured = process.env.SONNETDB_MEASUREMENT_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_MEASUREMENT_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const parent = await realpath(resolve(process.cwd(), '..', 'artifacts', 'wb31-validation-20261006'));
  if (!samePath(dirname(root), parent) || !/^measurement-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be a measurement-real run immediately inside artifacts/wb31-validation-20261006.');
  }
  const runInfo = await readFile(join(root, 'run.json'), { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  if (Buffer.byteLength(runInfo, 'utf8') > 65_536) throw new Error('Runner marker exceeded 64 KiB.');
  const marker = JSON.parse(runInfo) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'measurement-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('Measurement evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, measurement, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('Measurement evidence exceeded 1 MiB/file or 8 MiB/run.');
  if ([password, administrator?.token, writer?.token, administrator?.tokenId, writer?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) {
    throw new Error('Credential material must never be written to Measurement evidence.');
  }
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(content).digest('hex') });
}
