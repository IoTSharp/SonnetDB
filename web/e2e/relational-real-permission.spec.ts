import { randomBytes } from 'node:crypto';
import { readFile, stat } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Routed Web UI -> Vite proxy -> isolated real Kestrel. API login installs an
// actual session; this does not cover login UI, host readonly props, a whole-table
// snapshot, Server resource budgets, Studio, VS Code, installation or release.
const database = 'wb27';
const tableName = 'DeviceID_Main';
const columnNames = ['DeviceID', 'MixedCaseName'];
const username = 'wb27_writer';
const password = 'Workbench27:OnlyLocal!';
const profileId = 'wb27-real';
const profileName = 'WB27 real local Server';
const seedCount = 251;
const approvedRows = [[1_001, 'WB27:Approved:First'], [1_002, 'WB27:Approved:Second']];
const deniedId = 2_001;
const deniedPayload = 'WB27:Rejected:DraftPayload';
const sqlPath = `/v1/db/${database}/sql`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let administrator: AuthIdentity;
let writer: AuthIdentity;

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface SqlParameter { kind: number; integerValue?: number; stringValue?: string }
interface SqlStatement { sql: string; parameters?: Record<string, SqlParameter>; previewMaxRows?: number }
interface SqlBody extends Partial<SqlStatement> { statements?: SqlStatement[] }
interface EndFrame { type: 'end'; rowCount: number; recordsAffected: number; elapsedMs?: number; elapsedMilliseconds?: number; truncated?: boolean }
interface SqlFrames { columns: string[]; rows: unknown[][]; ends: EndFrame[] }
interface BrowserRequest { path: string; body: SqlBody; usesWriterToken: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }
interface HistoryEntry { action: string; status: string; database: string; target: string; connectionId: string; connectionName: string; recordsAffected: number; completeness: string; command: string }

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: 10_000 });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_RELATION_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_RELATION_REAL_BASE_URL is required; run the isolated real Relation runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Relation Server must be an explicit isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB27 isolated evidence', username: 'wb27_admin', password,
    bearerToken: `wb27_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb27_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  writer = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(writer).toMatchObject({ username, isSuperuser: false });
  expect(writer.token).toBeTruthy();
  expect(writer.tokenId).toBeTruthy();
  await adminSql(request, `CREATE TABLE "${tableName}" ("DeviceID" INT, "MixedCaseName" STRING, PRIMARY KEY ("DeviceID"))`);
  const values = Array.from({ length: seedCount }, (_, index) => `(${index + 1}, '${seedPayload(index + 1)}')`);
  const inserted = await adminSql(request, `INSERT INTO "${tableName}" ("DeviceID", "MixedCaseName") VALUES ${values.join(', ')}`);
  expect(inserted.ends).toHaveLength(1);
  expect(inserted.ends[0].recordsAffected).toBe(seedCount);
  const counted = await adminSql(request, `SELECT COUNT(*) FROM "${tableName}"`);
  expect(counted.rows).toEqual([[seedCount]]);
  const schema = await apiJson<{ tables: Array<{ name: string; columns: Array<{ name: string }> }> }>(request, 'GET', `/v1/db/${database}/schema`, undefined, 200, administrator.token);
  expect(schema.tables).toHaveLength(1);
  expect(schema.tables[0].name).toBe(tableName);
  expect(schema.tables[0].columns.map((column) => column.name)).toEqual(columnNames);
});

test.beforeEach(async ({ request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
});

test('real original-name SQL uses 50/200-row LIMIT OFFSET windows and exports only the current result', async ({ page }, testInfo) => {
  const { evidence, initial } = await openTable(page);
  assertBrowse(initial, 50, 0);
  await assertPage(page, 1, 50, 'longContent');
  await expect(surface(page).locator('.relation-grid')).toContainText(seedPayload(1));
  await expect(page.locator('.schema-item--table.is-active strong')).toHaveText(tableName);
  await expect(page.locator('.workspace-tab[aria-selected="true"]')).toContainText(tableName);
  const firstExport = await exportJson(page);
  expect(firstExport).toEqual(objects(initial.rows));
  expect(firstExport).toHaveLength(50);
  expect(firstExport.at(-1)).toEqual({ DeviceID: 50, MixedCaseName: seedPayload(50) });
  await closeResult(page);

  const next = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: 'Next', exact: true }).click());
  const second = await actualSql(next);
  assertBrowse(second, 50, 50);
  await assertPage(page, 2, 50, 'longContent');
  expect(evidence.requests.at(-1)?.body.parameters).toMatchObject({ limit: { integerValue: 50 }, offset: { integerValue: 50 } });
  const secondExport = await exportJson(page);
  expect(secondExport).toEqual(objects(second.rows));
  expect(secondExport[0]).toEqual({ DeviceID: 51, MixedCaseName: seedPayload(51) });
  await closeResult(page);

  const resized = await perform(page, sqlPath, async () => {
    await surface(page).locator('.relation-pager__size').click();
    await page.locator('.n-base-select-option').getByText('200 rows', { exact: true }).click();
  });
  const large = await actualSql(resized);
  assertBrowse(large, 200, 0);
  await assertPage(page, 1, 200, 'longContent');
  const largeExport = await exportJson(page);
  expect(largeExport).toEqual(objects(large.rows));
  expect(largeExport).toHaveLength(200);
  await closeResult(page);

  const tailResponse = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: 'Next', exact: true }).click());
  const tail = await actualSql(tailResponse);
  assertBrowse(tail, 51, 200);
  await assertPage(page, 2, 51, 'normal');
  await expect(surface(page).getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Previous', exact: true })).toBeEnabled();
  const tailExport = await exportJson(page);
  expect(tailExport).toEqual(objects(tail.rows));
  expect(tailExport[0]).toEqual({ DeviceID: 201, MixedCaseName: seedPayload(201) });
  expect(tailExport.at(-1)).toEqual({ DeviceID: seedCount, MixedCaseName: seedPayload(seedCount) });
  expect(writes(evidence)).toHaveLength(0);
  assertReadRequests(evidence);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-relation-windows', { body: JSON.stringify({ requests: safeRequests(evidence), serverRows: [initial.rows.length, second.rows.length, large.rows.length, tail.rows.length], exportedRows: [firstExport.length, secondExport.length, largeExport.length, tailExport.length], scope: 'current loaded LIMIT/OFFSET window only' }), contentType: 'application/json' });
});

test('real approved two-row batch has BEGIN/INSERT/INSERT/COMMIT terminal frames and is consumed once', async ({ page, request }, testInfo) => {
  const { evidence } = await openTable(page);
  await stageInsert(page, Number(approvedRows[0][0]), String(approvedRows[0][1]));
  const approval = page.getByRole('dialog', { name: 'Relation table edit batch' });
  await expect(approval).toContainText(String(approvedRows[0][1]));
  await approval.locator('.write-approval__actions').getByRole('button', { name: '返回编辑', exact: true }).click();
  await expect(approval).toHaveCount(0);
  await expect(surface(page).locator('.relation-toolbar__meta')).toContainText('1 staged edits');
  await stageInsert(page, Number(approvedRows[1][0]), String(approvedRows[1][1]));
  await expect(approval).toContainText(`${database}.${tableName}`);
  await expect(approval).toContainText(String(approvedRows[0][1]));
  await expect(approval).toContainText(String(approvedRows[1][1]));
  await expect(approval.locator('.write-approval-item')).toHaveCount(2);
  expect(writes(evidence)).toHaveLength(0);
  const response = await perform(page, `${sqlPath}/batch`, () => approval.getByRole('button', { name: '确认执行 2 项操作', exact: true }).click());
  const actual = await actualSql(response);
  expect(actual.rows).toEqual([]);
  expect(actual.ends).toHaveLength(4);
  expect(actual.ends.map((frame) => frame.recordsAffected)).toEqual([0, 1, 1, 2]);
  expect(actual.ends.map((frame) => frame.rowCount)).toEqual([0, 0, 0, 0]);
  await expect(approval).toHaveCount(0);
  await expect(surface(page).locator('.relation-toolbar__meta')).toContainText('0 staged edits');
  await expect(surface(page).getByRole('button', { name: 'Insert row', exact: true })).toBeEnabled();
  await expect(surface(page).getByRole('button', { name: 'Preview staged edits', exact: true })).toHaveCount(0);
  expect(writes(evidence)).toHaveLength(1);
  const batch = writes(evidence)[0];
  expect(batch.usesWriterToken).toBe(true);
  expect(batch.body.statements?.map((statement) => statement.sql)).toEqual(['BEGIN', expect.stringContaining(`INSERT INTO ${tableName} (`), expect.stringContaining(`INSERT INTO ${tableName} (`), 'COMMIT']);
  expect(batch.body.statements?.slice(1, 3).map((statement) => Object.values(statement.parameters ?? {}).map((parameter) => parameter.integerValue ?? parameter.stringValue))).toEqual(approvedRows);
  const stored = await adminSql(request, `SELECT "DeviceID", "MixedCaseName" FROM "${tableName}" WHERE "DeviceID" >= 1001 AND "DeviceID" <= 1002 ORDER BY "DeviceID"`);
  expect(stored.columns).toEqual(columnNames);
  expect(stored.rows).toEqual(approvedRows);
  const history = await latestHistory(page, 'insert, insert');
  expect(history).toMatchObject({ status: 'success', database, target: tableName, connectionId: profileId, connectionName: profileName, recordsAffected: 2, completeness: 'complete' });
  expect(history?.command).toContain('BEGIN');
  expect(history?.command).toContain('COMMIT');
  const refreshed = await perform(page, sqlPath, () => surface(page).getByRole('button', { name: 'Refresh', exact: true }).click());
  await actualSql(refreshed);
  await expect(surface(page).locator('.relation-pager')).toContainText('50 visible rows');
  expect(writes(evidence)).toHaveLength(1);
  await expect(approval).toHaveCount(0);

  // BEGIN has already written its end frame: a later deterministic SQL failure
  // can be an HTTP200 NDJSON error, so HTTP status alone cannot prove commit.
  await stageInsert(page, Number(approvedRows[0][0]), 'WB27:DuplicateMustNotReplace');
  const conflict = await perform(page, `${sqlPath}/batch`, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(conflict.status()).toBe(200);
  expect(conflict.headers()['content-type']).toContain('ndjson');
  const conflictFrames = boundedFrames(await conflict.text());
  const conflictEnds = conflictFrames.filter((frame) => frame && !Array.isArray(frame) && typeof frame === 'object' && (frame as { type?: string }).type === 'end');
  const conflictErrors = conflictFrames.filter(isErrorFrame);
  expect(conflictEnds).toHaveLength(2);
  expect(conflictEnds.map((frame) => (frame as EndFrame).recordsAffected)).toEqual([0, 1]);
  expect(conflictErrors).toHaveLength(1);
  expect(conflictFrames.at(-1)).toMatchObject({ error: 'table_unique_violation', code: 'table_unique_violation', message: expect.any(String) });
  await expect(approval).toHaveCount(0);
  await expect(surface(page).locator('.relation-toolbar__meta')).toContainText('0 staged edits');
  expect(writes(evidence)).toHaveLength(2);
  const failedHistory = await latestHistory(page, 'insert');
  expect(failedHistory).toMatchObject({ status: 'error', database, target: tableName, connectionId: profileId, recordsAffected: 0, completeness: 'partial' });
  const counted = await adminSql(request, `SELECT COUNT(*) FROM "${tableName}"`);
  expect(counted.rows).toEqual([[seedCount + approvedRows.length]]);
  const unchanged = await adminSql(request, `SELECT "DeviceID", "MixedCaseName" FROM "${tableName}" WHERE "DeviceID" >= 1001 AND "DeviceID" <= 1002 ORDER BY "DeviceID"`);
  expect(unchanged.rows).toEqual(approvedRows);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-relation-approved-batch', { body: JSON.stringify({ requests: safeRequests(evidence), status: response.status(), terminalFrames: actual.ends, administratorRows: stored.rows, history, conflictStatus: conflict.status(), conflictFrames, failedHistory, administratorCountAfterConflict: counted.rows, writeRequests: writes(evidence).length }), contentType: 'application/json' });
});

test('real revoke rejects old approval with HTTP403, clears payloads, and regrant/schema refresh cannot unlock or replay it', async ({ page, request }, testInfo) => {
  const { evidence } = await openTable(page);
  await expect(surface(page).locator('.relation-grid')).toContainText(seedPayload(1));
  await surface(page).getByRole('button', { name: 'DDL', exact: true }).click();
  await expect(surface(page).locator('.relation-ddl textarea')).toHaveValue(new RegExp(`CREATE TABLE ${tableName} \\(`, 'u'));
  await surface(page).getByRole('button', { name: '数据', exact: true }).click();
  await surface(page).getByPlaceholder('Filter', { exact: true }).fill('WB27:OldFilterDraft');
  await stageInsert(page, deniedId, deniedPayload);
  const approval = page.getByRole('dialog', { name: 'Relation table edit batch' });
  await expect(approval).toContainText(deniedPayload);
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  // SqlEndpoints requires READ before invoking BEGIN or any SQL statement;
  // a fully revoked principal therefore receives HTTP403 before execution.
  const response = await perform(page, `${sqlPath}/batch`, () => approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(response.status()).toBe(403);
  const rejection = await response.json() as { code?: string; error?: string };
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  await hiddenPermissionPayload(page);
  expect(writes(evidence)).toHaveLength(1);
  expect(writes(evidence)[0].usesWriterToken).toBe(true);
  const absent = await adminSql(request, `SELECT "DeviceID", "MixedCaseName" FROM "${tableName}" WHERE "DeviceID" = ${deniedId}`);
  expect(absent.rows).toEqual([]);

  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const lockedRequests = evidence.requests.length;
  const schema = page.waitForResponse((result) => result.request().method() === 'GET'
    && decodeURIComponent(new URL(result.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await page.getByTitle('刷新资源', { exact: true }).click();
  expect((await schema).status()).toBe(200);
  await expect(page.getByTitle('刷新资源', { exact: true }).locator('svg')).not.toHaveClass(/is-spinning/u);
  await hiddenPermissionPayload(page);
  await surface(page).getByRole('button', { name: 'DDL', exact: true }).click();
  await hiddenPermissionPayload(page);
  await expect(surface(page).locator('.relation-ddl')).toHaveCount(0);
  await surface(page).getByRole('button', { name: '数据', exact: true }).click();
  await hiddenPermissionPayload(page);
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const stillAbsent = await adminSql(request, `SELECT "DeviceID", "MixedCaseName" FROM "${tableName}" WHERE "DeviceID" = ${deniedId}`);
  expect(stillAbsent.rows).toEqual([]);
  expect(await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, writer.token)).toBe(true);
  expect(evidence.overflow).toBe(false);
  await testInfo.attach('real-relation-revoked-approval', { body: JSON.stringify({ requests: safeRequests(evidence), deniedStatus: response.status(), administratorRejectedRows: absent.rows, regrant: 'READ', schemaStatus: 200, stillLocked: true, rejectedRowsAfterRefresh: stillAbsent.rows, sameToken: true, writeRequests: writes(evidence).length }), contentType: 'application/json' });
});

function surface(page: Page) { return page.getByTestId('workbench-table'); }
function resultPanel(page: Page) { return page.locator('.workbench-result-panel'); }
function seedPayload(id: number) { return `WB27:Payload:${String(id).padStart(4, '0')}`; }
function objects(rows: unknown[][]) { return rows.map((row) => ({ DeviceID: row[0], MixedCaseName: row[1] })); }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => entry.path === `${sqlPath}/batch`); }
function safeRequests(evidence: BrowserEvidence) { return evidence.requests.map(({ path, body, usesWriterToken }) => ({ path, body, usesWriterToken })); }

async function openTable(page: Page): Promise<{ evidence: BrowserEvidence; initial: SqlFrames }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    if (request.method() !== 'POST' || ![sqlPath, `${sqlPath}/batch`].includes(path)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ path, body: request.postDataJSON() as SqlBody, usesWriterToken: request.headers().authorization === `Bearer ${writer.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{
      id, name, kind: 'managed-local', baseUrl: '/', defaultDatabase: db,
      tokenMode: 'current-session', createdAt: 1, updatedAt: 1,
    }], activeProfileId: id, activeDatabase: db }));
  }, { identity: writer, db: database, id: profileId, name: profileName });
  const response = page.waitForResponse(matches(sqlPath), { timeout: responseTimeout });
  const query = new URLSearchParams({ tool: 'table', database, model: 'table', node: tableName });
  await page.goto(`/admin/app/sql?${query}`);
  const initial = await actualSql(await response);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', tableName);
  await expect(surface(page)).toHaveAttribute('data-legacy-key', `table:${tableName}`);
  await expect(surface(page).locator('.relation-toolbar__title')).toHaveText(tableName);
  await assertPage(page, 1, 50, 'longContent');
  await expect(surface(page).getByRole('button', { name: 'Insert row', exact: true })).toBeEnabled();
  return { evidence, initial };
}

function matches(path: string) {
  return (response: Response) => response.request().method() === 'POST'
    && decodeURIComponent(new URL(response.url()).pathname) === path;
}
async function perform(page: Page, path: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(path), { timeout: responseTimeout });
  await start();
  return response;
}
async function actualSql(response: Response): Promise<SqlFrames> {
  expect(response.status()).toBe(200);
  expect(response.headers()['content-type']).toContain('ndjson');
  return parseFrames(await response.text());
}
function parseFrames(text: string): SqlFrames {
  const frames = boundedFrames(text);
  const errors = frames.filter(isErrorFrame);
  expect(errors, 'A successful real SQL response must not contain an error frame.').toEqual([]);
  const objectFrames = frames.filter((frame) => frame && !Array.isArray(frame) && typeof frame === 'object') as Array<{ type?: string; columns?: string[] } & Partial<Omit<EndFrame, 'type'>>>;
  expect(objectFrames.every((frame) => frame.type === 'meta' || frame.type === 'end')).toBe(true);
  expect(objectFrames.at(-1)?.type).toBe('end');
  const columns = objectFrames.find((frame) => frame.type === 'meta')?.columns ?? [];
  const rows = frames.filter(Array.isArray) as unknown[][];
  expect(rows.every((row) => row.length === columns.length)).toBe(true);
  const ends = objectFrames.filter((frame) => frame.type === 'end') as EndFrame[];
  expect(ends.length).toBeGreaterThan(0);
  expect(ends.every((frame) => Number.isSafeInteger(frame.rowCount) && frame.rowCount >= 0
    && Number.isSafeInteger(frame.recordsAffected) && frame.recordsAffected >= -1
    && Number.isFinite(frame.elapsedMs ?? frame.elapsedMilliseconds)
    && (frame.elapsedMs ?? frame.elapsedMilliseconds ?? -1) >= 0)).toBe(true);
  expect(ends.reduce((count, frame) => count + frame.rowCount, 0)).toBe(rows.length);
  return { columns, rows, ends };
}
function boundedFrames(text: string): unknown[] {
  if (Buffer.byteLength(text, 'utf8') > 262_144) throw new Error('SQL evidence exceeded 256 KiB.');
  const lines = text.trim().split(/\r?\n/u);
  if (lines.length > 1_024) throw new Error('SQL evidence exceeded 1024 NDJSON frames.');
  return lines.map((line) => JSON.parse(line) as unknown);
}
function isErrorFrame(frame: unknown): boolean {
  if (!frame || Array.isArray(frame) || typeof frame !== 'object') return false;
  const value = frame as { type?: string; error?: string; code?: string; message?: string };
  return value.type === 'error' || (typeof value.message === 'string' && (typeof value.error === 'string' || typeof value.code === 'string'));
}
function assertBrowse(result: SqlFrames, count: number, offset: number): void {
  expect(result.columns).toEqual(columnNames);
  expect(result.rows).toEqual(Array.from({ length: count }, (_, index) => [offset + index + 1, seedPayload(offset + index + 1)]));
  expect(result.ends).toHaveLength(1);
  expect(result.ends[0]).toMatchObject({ rowCount: count, recordsAffected: -1 });
}
function assertReadRequests(evidence: BrowserEvidence): void {
  const reads = evidence.requests.filter((entry) => entry.path === sqlPath);
  expect(reads.length).toBeGreaterThanOrEqual(4);
  // Valid identifiers are emitted without quotes by formatSqlIdentifier;
  // assert exact original spelling rather than assuming quoted colon fixtures.
  const originalNameSql = `SELECT DeviceID, MixedCaseName\nFROM ${tableName}\nORDER BY DeviceID ASC\nLIMIT @limit\nOFFSET @offset;`;
  expect(reads.every(({ body, usesWriterToken }) => usesWriterToken && /^SELECT\b/iu.test(body.sql ?? '')
    && body.sql === originalNameSql
    && /LIMIT @limit\s+OFFSET @offset/iu.test(body.sql) && body.previewMaxRows === body.parameters?.limit?.integerValue
    && Number.isSafeInteger(body.previewMaxRows) && (body.previewMaxRows ?? 0) <= 200)).toBe(true);
  expect(reads.some(({ body }) => body.parameters?.limit?.integerValue === 50 && body.parameters.offset?.integerValue === 50)).toBe(true);
  expect(reads.some(({ body }) => body.parameters?.limit?.integerValue === 200 && body.parameters.offset?.integerValue === 200)).toBe(true);
}
async function assertPage(page: Page, number: number, count: number, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
  await expect(surface(page).locator('.relation-pager')).toContainText(`${count} visible rows`);
  await expect(surface(page).locator('.relation-pager')).toContainText(`Page ${number}`);
}
async function stageInsert(page: Page, id: number, payload: string): Promise<void> {
  await surface(page).getByRole('button', { name: 'Insert row', exact: true }).click();
  const form = surface(page).locator('.relation-insert');
  await expect(form).toBeVisible();
  await form.locator('.relation-field').filter({ hasText: 'DeviceID' }).locator('input').fill(String(id));
  await form.locator('.relation-field').filter({ hasText: 'MixedCaseName' }).locator('input').fill(payload);
  await form.getByRole('button', { name: 'Stage insert', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Relation table edit batch' })).toBeVisible();
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', tableName);
  await expect(surface(page).locator('.relation-grid-shell')).toHaveCount(0);
  await expect(surface(page).locator('.relation-column-title')).toHaveCount(0);
  await expect(surface(page).locator('.relation-insert')).toHaveCount(0);
  await expect(surface(page).locator('.relation-ddl')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Refresh', exact: true })).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: 'Insert row', exact: true })).toHaveCount(0);
  await expect(surface(page).getByPlaceholder('Filter', { exact: true })).toHaveCount(0);
  await expect(surface(page).locator('.relation-toolbar__meta')).toContainText('0 staged edits');
  await expect(surface(page)).not.toContainText(seedPayload(1));
  await expect(surface(page)).not.toContainText(deniedPayload);
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
  if (!(await resultPanel(page).isVisible())) await page.getByTitle('查看结果', { exact: true }).click();
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
    if (!Array.isArray(parsed) || parsed.length > 200) throw new Error('Relation preview JSON export exceeded 200 rows.');
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
  await adminSql(request, sql, '/v1/sql');
}
async function adminSql(request: APIRequestContext, sql: string, path = sqlPath): Promise<SqlFrames> {
  const response = await request.post(new URL(path, serverOrigin).href, {
    headers: { Authorization: `Bearer ${administrator.token}` }, data: { sql, previewMaxRows: 1_000 }, timeout: apiTimeout, maxRetries: 0,
  });
  try {
    expect(response.status(), 'The real administrator SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    return parseFrames(await response.text());
  } finally { await response.dispose(); }
}
