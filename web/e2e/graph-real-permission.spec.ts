import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Locator, type Page, type Response } from '@playwright/test';
import type { GraphExportDocument, GraphImportResponse, GraphMutationResponse, GraphOperationsOverview, GraphProperty, GraphVertex, GraphVisualization } from '../src/api/graphs';

// Normal routed Vue -> real Vite proxy -> isolated Kestrel. API login installs
// an ordinary non-superuser session. Visualization samples vertices, then the
// client bounds total canvas elements; export owns a separate statement snapshot.
// Public ECharts getInstanceByDom/getOption only observes the normal UI's chart.
// No component/setup/prop/program-entry harness is installed or invoked.
// These safe-number IDs, current snapshots and one vertex Upsert do not prove
// full Int64, edge/delete/import/maintenance, explicit recovery or other hosts.
const database = 'wb35';
const graph = 'DeviceGraph_Original';
const username = 'wb35_operator';
const password = 'Workbench35:OnlyLocal!';
const profileId = 'wb35-real';
const profileName = 'WB35 real local Server';
const vertexCount = 151;
const edgeCount = 150;
const editedId = 1;
const approvedValue = 'WB35ApprovedVertexProperty_Original';
const deniedValue = 'WB35RejectedVertexPropertyMustDisappear';
const importDraftValue = 'WB35VisibleImportDraftMustDisappear';
const graphPath = `/v1/db/${database}/graphs/${graph}`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let operator: AuthIdentity;
let seedTerminal: GraphImportResponse;
let seeded: GraphExportDocument;
let approvedVertex: GraphVertex;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface BrowserRequest { method: string; path: string; query: Record<string, string>; body: Record<string, unknown> | null; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean; echartsModuleUrl: string }
interface CanvasSnapshot { ownsCurrentDom: boolean; vertices: string[]; edges: Array<{ id: string; source: string; target: string }> }
interface HistoryEntry {
  title: string; action: string; model: string; status: string; database: string; target: string; connectionId: string;
  connectionName: string; command: string; summary: string; rowCount?: number; recordsAffected?: number; completeness?: string;
}

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_GRAPH_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_GRAPH_REAL_BASE_URL is required; use the isolated real Graph runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real Graph Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB35 isolated evidence', username: 'wb35_admin', password,
    bearerToken: `wb35_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb35_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  operator = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(operator).toMatchObject({ username, isSuperuser: false });
  expect(operator.token).toBeTruthy();
  expect(operator.tokenId).toBeTruthy();
  const created = await apiJson<{ name: string; storageId: string; recordFormatVersion: number }>(request, 'POST',
    `/v1/db/${database}/graphs`, { name: graph }, 201, administrator.token);
  expect(created.name).toBe(graph);
  expect(created.storageId).toBeTruthy();
  const vertices = Array.from({ length: vertexCount }, (_, index) => ({ id: index + 1, expectedElementVersion: 0,
    labels: [10], properties: seedProperties(index), uniquePropertyIds: [] }));
  const edges = Array.from({ length: edgeCount }, (_, index) => ({ id: 1001 + index, expectedElementVersion: 0,
    sourceId: index + 1, targetId: index + 2, labelId: 30,
    properties: [{ propertyId: 40, value: { kind: 4, string: `WB35EdgeProperty_${index}` } }], uniquePropertyIds: [] }));
  seedTerminal = await apiJson<GraphImportResponse>(request, 'POST', `${graphPath}/import`, { requestId: randomUUID(), vertices, edges }, 200, administrator.token);
  expect(seedTerminal).toMatchObject({ isDuplicate: false, vertexCount, edgeCount });
  assertSafeSequence(seedTerminal.sequence);
  seeded = await apiJson<GraphExportDocument>(request, 'GET', `${graphPath}/operations/export?maxElements=1000`, undefined, 200, administrator.token);
  expect(seeded).toMatchObject({ snapshotSequence: seedTerminal.sequence, truncated: false, elementCount: vertexCount + edgeCount });
  expect(seeded.vertices).toHaveLength(vertexCount);
  expect(seeded.edges).toHaveLength(edgeCount);
  expect(seeded.vertices.map((vertex) => vertex.id)).toEqual(vertices.map((vertex) => vertex.id));
  expect(seeded.edges.map((edge) => ({ id: edge.id, sourceId: edge.sourceId, targetId: edge.targetId, labelId: edge.labelId })))
    .toEqual(edges.map((edge) => ({ id: edge.id, sourceId: edge.sourceId, targetId: edge.targetId, labelId: edge.labelId })));
  const verificationDeadline = Date.now() + apiTimeout;
  let verifiedVertices = 0;
  for (let index = 0; index < Math.min(vertexCount, seeded.vertices.length) && Date.now() < verificationDeadline; index += 1) {
    const vertex = seeded.vertices[index];
    expect(vertex.labels).toEqual([10]);
    expect(vertex.properties).toHaveLength(3);
    expect(vertex.properties.map((property) => ({ propertyId: property.propertyId, value: activeValue(property.value) })))
      .toEqual(seedProperties(index));
    expect(Number.isSafeInteger(vertex.elementVersion) && vertex.elementVersion > 0).toBe(true);
    verifiedVertices += 1;
  }
  expect(verifiedVertices).toBe(vertexCount);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false,
      timeoutsMs: { test: 120_000, controlApi: apiTimeout, browserResponseAndDownloadEvent: responseTimeout, productAxios: 30_000 },
      scope: 'Graph Beta with safe-number identities; visualization and independent export snapshots are separate contracts.' });
  }
});

test('ordinary READ preserves Graph Beta identity, bounded canvas10/1000 and typed vertex with exact independent JSON snapshot exports and real export history', async ({ page, request }) => {
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const { evidence, overview, initial } = await openGraph(page);
  const initialCanvas = await assertCanvas(page, evidence, initial, 250);
  const smallResponse = await resample(page, 10);
  const small = await actualJson<GraphVisualization>(smallResponse, 200);
  assertVisualization(small, 10);
  const smallCanvas = await assertCanvas(page, evidence, small, 10);
  expect(smallCanvas.vertices).toHaveLength(10);
  expect(smallCanvas.edges).toHaveLength(0);
  const largeResponse = await resample(page, 1000);
  const large = await actualJson<GraphVisualization>(largeResponse, 200);
  assertVisualization(large, 1000);
  const largeCanvas = await assertCanvas(page, evidence, large, 1000);
  expect(largeCanvas.vertices.length + largeCanvas.edges.length).toBe(vertexCount + edgeCount);
  const read = await readVertex(page, editedId);
  expect(read).toEqual(seeded.vertices[0]);
  const beforeExports = await historyEntries(page);
  expect(beforeExports.filter((entry) => entry.model === 'graph')).toEqual([]);

  const smallExport = await exportGraph(page, 10);
  assertExport(smallExport.document, 10);
  expect(smallExport.document).toMatchObject({ truncated: true, elementCount: 10 });
  const smallHistory = await latestHistory(page, 'Graph JSON export');
  assertExportHistory(smallHistory, 10);
  const largeExport = await exportGraph(page, 1000);
  assertExport(largeExport.document, 1000);
  expect(largeExport.document).toEqual(seeded);
  const largeHistory = await latestHistory(page, 'Graph JSON export');
  assertExportHistory(largeHistory, 1000);
  expect(new URL(page.url()).searchParams.get('node')).toBe(graph);
  expect(writes(evidence)).toHaveLength(0);
  assertEvidence(evidence);
  await persistEvidence('real-graph-read-canvas-export', { requests: evidence.requests, seedTerminal, overview, initial, initialCanvas,
    small, smallCanvas, large, largeCanvas, typedVertex: read, smallExport, smallHistory, largeExport, largeHistory,
    databaseGrant: 'READ', isSuperuser: false, graphBeta: true,
    scope: 'Normal canvas with public read-only ECharts observation. Exports are separate Server snapshots and history records; no browse history, graph pagination or Server resource-budget claim.' });
});

test('ordinary WRITE normally approves one existing safe vertex Upsert with sequence/duplicate terminal, typed administrator Get and original operation history', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence } = await openGraph(page);
  const original = await readVertex(page, editedId);
  expect(original).toEqual(seeded.vertices[0]);
  const approvedProperties = replaceStringProperty(original.properties, approvedValue);
  await propertiesEditor(page).fill(JSON.stringify(approvedProperties, null, 2));
  const before = writes(evidence).length;
  await stageVertex(page, original);
  expect(writes(evidence)).toHaveLength(before);
  await expect(confirmButton(page)).toBeEnabled();
  const refreshed = page.waitForResponse(matches('GET', `/vertices/${editedId}`), { timeout: responseTimeout });
  const response = await perform(page, 'PUT', `/vertices/${editedId}`, () => confirmButton(page).click());
  const terminal = await actualJson<GraphMutationResponse>(response, 200);
  assertSafeSequence(terminal.sequence);
  expect(terminal.sequence).toBeGreaterThan(seedTerminal.sequence);
  expect(terminal.isDuplicate).toBe(false);
  const body = response.request().postDataJSON() as Record<string, unknown>;
  expect(body).toMatchObject({ id: editedId, expectedElementVersion: original.elementVersion,
    labels: original.labels, properties: approvedProperties, uniquePropertyIds: [] });
  expect(body.requestId).toMatch(/^[0-9a-f-]{36}$/iu);
  const reread = await actualJson<GraphVertex>(await refreshed, 200);
  await assertVertexEditor(page, reread);
  await expect(approval(page)).toHaveCount(0);
  approvedVertex = await administratorGet(request, editedId);
  expect(approvedVertex).toEqual(reread);
  expect(approvedVertex.id).toBe(original.id);
  expect(approvedVertex.labels).toEqual(original.labels);
  expect(approvedVertex.properties).toEqual(approvedProperties);
  expect(Number.isSafeInteger(approvedVertex.elementVersion)).toBe(true);
  expect(approvedVertex.elementVersion).toBe(original.elementVersion + 1);
  const history = await latestHistory(page, 'Graph 元素 Upsert');
  assertOperationHistory(history, 'success', original.elementVersion);
  expect(history?.summary).toBe(`sequence ${terminal.sequence} · duplicate false`);
  expect(writes(evidence)).toHaveLength(1);
  assertEvidence(evidence);
  await persistEvidence('real-graph-approved-vertex-upsert', { requests: evidence.requests, original, approvedProperties,
    mutation: { status: response.status(), headers: safeHeaders(response), request: body, terminal }, reread, administratorVertex: approvedVertex, history,
    databaseGrant: 'WRITE', isSuperuser: false, approvalsConsumed: 1, writeRequests: 1,
    scope: 'One existing safe-number vertex Upsert. Administrator Get proves original ID/labels, typed properties and a newer version; complete mutation/Int64/import/maintenance matrices are separate.' });
});

test('real REVOKE rejects an old vertex approval with403, clears visible canvas/overview/editor/import draft and same-token READ/Schema200/internal tabs cannot recover or replay', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence, overview, initial } = await openGraph(page);
  const oldCanvas = await assertCanvas(page, evidence, initial, 250);
  expect(oldCanvas.vertices).toContain(String(editedId));
  await graphTab(page, 'Import / export');
  const importDraft = JSON.stringify({ vertices: [{ id: 9001, expectedElementVersion: 0, labels: [10],
    properties: [{ propertyId: 20, value: { kind: 4, string: importDraftValue } }], uniquePropertyIds: [] }], edges: [] });
  await importEditor(page).fill(importDraft);
  await expect(importEditor(page)).toHaveValue(importDraft);
  expect(writes(evidence)).toHaveLength(0);
  const original = await readVertex(page, editedId);
  expect(original).toEqual(approvedVertex);
  await expect(propertiesEditor(page)).toHaveValue(new RegExp(approvedValue, 'u'));
  const rejectedProperties = replaceStringProperty(original.properties, deniedValue);
  await propertiesEditor(page).fill(JSON.stringify(rejectedProperties, null, 2));
  await expect(surface(page).locator('.graph-head__title-row')).toContainText(`snapshot ${overview.snapshotSequence}`);
  await stageVertex(page, original);
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  await expect(confirmButton(page)).toBeEnabled();
  const response = await perform(page, 'PUT', `/vertices/${editedId}`, () => confirmButton(page).click());
  const rejection = await actualJson<{ code?: string; error?: string }>(response, 403);
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  expect(response.request().postDataJSON()).toMatchObject({ id: editedId, expectedElementVersion: original.elementVersion,
    labels: original.labels, properties: rejectedProperties, uniquePropertyIds: [] });
  await hiddenPermissionPayload(page);
  const rejectionHistory = await latestHistory(page, 'Graph 元素 Upsert');
  assertOperationHistory(rejectionHistory, 'error', original.elementVersion);
  expect(rejectionHistory?.summary).toBe('Graph 写入被拒绝。');
  const afterRejection = await administratorGet(request, editedId);
  expect(afterRejection).toEqual(approvedVertex);
  expect(afterRejection.properties.some((property) => property.value.string === deniedValue)).toBe(false);
  expect(afterRejection.properties.find((property) => property.propertyId === 20)?.value.string).toBe(approvedValue);
  const lockedRequests = evidence.requests.length;
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const schemaStatus = await refreshRealSchema(page);
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Canvas');
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Schema & diagnostics');
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Restricted edit');
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Import / export');
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Maintenance');
  await hiddenPermissionPayload(page);
  await graphTab(page, 'Canvas');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByTitle('刷新 Graph 运维数据', { exact: true })).toBeDisabled();
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const sameSession = await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, operator.token);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-graph-revoked-vertex-approval', { requests: evidence.requests, overview, initial, oldCanvas,
    visibleImportDraft: importDraft, original, rejectedProperties, deniedStatus: response.status(), rejection, rejectionHistory, afterRejection,
    databaseGrantBeforeRevoke: 'WRITE', regrant: 'READ', isSuperuser: false, schemaStatus, sameSession,
    stillLocked: true, approvalConsumed: true, writeRequests: 1, rejectedValueAbsentFromAdministratorGet: true,
    canvasOverviewElementEditorAndVisibleImportDraftRemoved: true,
    scope: 'Visible import text was filled normally and never staged. Only the vertex approval was dispatched. No maintenance approval, explicit recovery, replay or other host claim.' });
});

function surface(page: Page) { return page.getByTestId('workbench-graph'); }
function approval(page: Page) { return page.getByRole('dialog', { name: 'Graph 元素 Upsert' }); }
function confirmButton(page: Page) { return approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }); }
function propertiesEditor(page: Page) {
  return surface(page).locator('.editor-form label').filter({ hasText: 'Properties（typed JSON 数组）' }).locator('textarea');
}
function importEditor(page: Page) { return surface(page).getByPlaceholder('{ "vertices": [], "edges": [] }', { exact: true }); }
function seedProperties(index: number): GraphProperty[] {
  return [{ propertyId: 20, value: { kind: 4, string: `WB35SeedProperty_Original_${String(index).padStart(4, '0')}` } },
    { propertyId: 21, value: { kind: 1, int64: index } }, { propertyId: 22, value: { kind: 3, boolean: index % 2 === 0 } }];
}
function activeValue(value: GraphProperty['value']) {
  if (value.kind === 4) return { kind: 4, string: value.string };
  if (value.kind === 1) return { kind: 1, int64: value.int64 };
  if (value.kind === 3) return { kind: 3, boolean: value.boolean };
  throw new Error('The bounded seed contains only String/Int64/Boolean properties.');
}
function replaceStringProperty(properties: GraphProperty[], value: string): GraphProperty[] {
  return properties.map((property) => property.propertyId === 20 ? { ...property, value: { ...property.value, kind: 4, string: value } } : property);
}
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => !['GET', 'HEAD'].includes(entry.method)); }
function matches(method: string, suffix: string) {
  return (response: Response) => response.request().method() === method && decodeURIComponent(new URL(response.url()).pathname) === `${graphPath}${suffix}`;
}
async function perform(page: Page, method: string, suffix: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(method, suffix), { timeout: responseTimeout });
  await start();
  return response;
}
async function graphTab(page: Page, name: string): Promise<void> {
  // The normal button's accessible name includes its live count badge (for
  // example "Canvas 151"). Match its exact visible label span inside the
  // scoped button, so all five tabs remain stable as counts change or clear.
  const tab = surface(page).locator('.workbench-section-tabs').getByRole('button')
    .filter({ has: page.getByText(name, { exact: true }) });
  await expect(tab).toHaveCount(1);
  await tab.click();
}
async function openGraph(page: Page): Promise<{ evidence: BrowserEvidence; overview: GraphOperationsOverview; initial: GraphVisualization }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false, echartsModuleUrl: '' };
  page.on('request', (request) => {
    const url = new URL(request.url());
    if (request.method() === 'GET' && /\/node_modules\/\.vite\/deps\/echarts_core\.js$/u.test(url.pathname)) evidence.echartsModuleUrl = url.href;
    const path = decodeURIComponent(url.pathname);
    // Schema may refresh the parent Explorer Graph list. Count the exact
    // selected Graph's read/write paths to prove its latch does not replay.
    if (!path.startsWith(`${graphPath}/`)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ method: request.method(), path, query: Object.fromEntries(url.searchParams),
      body: request.postData() ? request.postDataJSON() as Record<string, unknown> : null,
      usedSession: request.headers().authorization === `Bearer ${operator.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: operator, db: database, id: profileId, name: profileName });
  const receivedOverview = page.waitForResponse(matches('GET', '/operations/overview'), { timeout: responseTimeout });
  const receivedVisualization = page.waitForResponse(matches('GET', '/operations/visualization'), { timeout: responseTimeout });
  await page.goto(`/admin/app/sql?${new URLSearchParams({ tool: 'graph', database, model: 'graph', node: graph })}`);
  const overview = await actualJson<GraphOperationsOverview>(await receivedOverview, 200);
  expect(overview.graph.name).toBe(graph);
  expect(overview).toMatchObject({ vertexCount, edgeCount, capabilities: { boundedVisualization: true, restrictedEditing: true, jsonImportExport: true } });
  assertSafeSequence(overview.snapshotSequence);
  const response = await receivedVisualization;
  expect(new URL(response.request().url()).searchParams.get('limit')).toBe('250');
  const initial = await actualJson<GraphVisualization>(response, 200);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `graph:${graph}`);
  await expect(surface(page).locator('.graph-head__title-row strong')).toHaveText(graph);
  await expect(surface(page).getByTestId('graph-beta')).toHaveText('Graph Beta');
  await expect(surface(page).getByTitle('刷新 Graph 运维数据', { exact: true })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  expect(evidence.echartsModuleUrl).toBeTruthy();
  return { evidence, overview, initial };
}
async function resample(page: Page, limit: number): Promise<Response> {
  await graphTab(page, 'Canvas');
  await fillNumber(surface(page).locator('.limit-control input'), limit);
  const button = surface(page).getByRole('button', { name: '重新采样', exact: true });
  await expect(button).toBeEnabled();
  const response = await perform(page, 'GET', '/operations/visualization', () => button.click());
  expect(new URL(response.request().url()).searchParams.get('limit')).toBe(String(limit));
  return response;
}
function assertVisualization(actual: GraphVisualization, limit: number): void {
  assertSafeSequence(actual.snapshotSequence);
  expect(actual.snapshotSequence).toBe(seedTerminal.sequence);
  expect(actual.vertices).toEqual(seeded.vertices.slice(0, limit));
  const ids = new Set(actual.vertices.map((vertex) => vertex.id));
  expect(actual.edges).toEqual(seeded.edges.filter((edge) => ids.has(edge.sourceId) && ids.has(edge.targetId)));
  expect(actual.vertices.length).toBeLessThanOrEqual(limit);
  expect(actual.edges.length).toBeLessThanOrEqual(limit * 2);
  expect(actual.edges.every((edge) => ids.has(edge.sourceId) && ids.has(edge.targetId))).toBe(true);
  expect(actual.truncated).toBe(limit < vertexCount);
}
function retainedCanvas(actual: GraphVisualization, limit: number) {
  const vertices = actual.vertices.slice(0, limit);
  const ids = new Set(vertices.map((vertex) => vertex.id));
  const edges = actual.edges.slice(0, Math.max(0, limit - vertices.length)).filter((edge) => ids.has(edge.sourceId) && ids.has(edge.targetId));
  return { vertices, edges, truncated: actual.truncated || vertices.length < actual.vertices.length || edges.length < actual.edges.length };
}
async function assertCanvas(page: Page, evidence: BrowserEvidence, actual: GraphVisualization, limit: number): Promise<CanvasSnapshot> {
  const expected = retainedCanvas(actual, limit);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await expect(surface(page).getByRole('button', { name: '重新采样', exact: true })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.graph-snapshot-note span').nth(0)).toHaveText(`${expected.vertices.length} vertices · ${expected.edges.length} edges`);
  await expect(surface(page).locator('.graph-snapshot-note span').nth(1)).toHaveText(`snapshot ${actual.snapshotSequence}`);
  await expect(surface(page).getByTestId('graph-preview-budget')).toContainText(`客户端画布总元素最多 ${limit}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', expected.truncated ? 'longContent' : 'normal');
  if (expected.truncated) await expect(surface(page)).toContainText('当前画布已截断 truncated');
  const snapshot = await publicCanvasSnapshot(page, evidence.echartsModuleUrl);
  expect(snapshot.ownsCurrentDom).toBe(true);
  expect(snapshot.vertices).toEqual(expected.vertices.map((vertex) => String(vertex.id)));
  expect(snapshot.edges).toEqual(expected.edges.map((edge) => ({ id: String(edge.id), source: String(edge.sourceId), target: String(edge.targetId) })));
  expect(snapshot.vertices.length + snapshot.edges.length).toBeLessThanOrEqual(limit);
  expect(snapshot.edges.every((edge) => snapshot.vertices.includes(edge.source) && snapshot.vertices.includes(edge.target))).toBe(true);
  return snapshot;
}
async function publicCanvasSnapshot(page: Page, moduleUrl: string): Promise<CanvasSnapshot> {
  if (!moduleUrl || new URL(moduleUrl).origin !== new URL(page.url()).origin) throw new Error('The observed ECharts module must belong to the actual UI origin.');
  return page.evaluate(async (url) => {
    // This module was requested by the normal Graph component. Importing the
    // same URL reuses it; only documented observation APIs are called.
    const echarts = await import(/* @vite-ignore */ url);
    const target = document.querySelector('[data-testid="workbench-graph"] .graph-canvas');
    const instance = target ? echarts.getInstanceByDom(target) : undefined;
    const option = instance?.getOption();
    const series = option?.series?.[0];
    const data = series?.data ?? [];
    const edges = series?.edges ?? [];
    if (!Array.isArray(data) || !Array.isArray(edges) || data.length + edges.length > 1000) throw new Error('The actual Graph canvas exceeded the observation element budget.');
    return { ownsCurrentDom: Boolean(instance && instance.getDom() === target),
      vertices: data.map((item: { id: string }) => String(item.id)),
      edges: edges.map((item: { id: string; source: string; target: string }) => ({ id: String(item.id), source: String(item.source), target: String(item.target) })) };
  }, moduleUrl);
}
async function readVertex(page: Page, id: number): Promise<GraphVertex> {
  await graphTab(page, 'Restricted edit');
  await fillNumber(surface(page).locator('.editor-rail label').filter({ hasText: '元素 ID' }).locator('input'), id);
  const button = surface(page).getByRole('button', { name: '读取当前版本', exact: true });
  await expect(button).toBeEnabled();
  const response = await perform(page, 'GET', `/vertices/${id}`, () => button.click());
  const vertex = await actualJson<GraphVertex>(response, 200);
  expect(vertex.id).toBe(id);
  expect(Number.isSafeInteger(vertex.id) && vertex.id > 0).toBe(true);
  expect(Number.isSafeInteger(vertex.elementVersion) && vertex.elementVersion > 0).toBe(true);
  await assertVertexEditor(page, vertex);
  return vertex;
}
async function assertVertexEditor(page: Page, vertex: GraphVertex): Promise<void> {
  await expect(surface(page).getByRole('button', { name: '读取当前版本', exact: true })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.editor-contract div').filter({ hasText: 'expectedVersion' }).locator('dd')).toHaveText(String(vertex.elementVersion));
  expect(JSON.parse(await propertiesEditor(page).inputValue())).toEqual(vertex.properties);
  expect(JSON.parse(await surface(page).locator('.editor-form label').filter({ hasText: 'Labels（JSON 数组）' }).locator('textarea').inputValue())).toEqual(vertex.labels);
}
async function stageVertex(page: Page, original: GraphVertex): Promise<void> {
  const button = surface(page).getByRole('button', { name: '暂存 Upsert', exact: true });
  await expect(button).toBeEnabled();
  await button.click();
  await expect(approval(page)).toBeVisible();
  await expect(approval(page)).toContainText(`${database}.${graph}`);
  await expect(approval(page)).toContainText(`upsert vertex ${original.id} expectedVersion=${original.elementVersion}`);
  await expect(approval(page)).toContainText('确认执行 1 项操作');
  await expect(confirmButton(page)).toBeEnabled();
}
async function fillNumber(input: Locator, value: number): Promise<void> {
  // Naive UI keeps a typed number as a draft until Enter or blur. Use normal
  // keyboard commit and focus movement before observing dependent controls.
  await input.fill(String(value));
  await input.press('Enter');
  await input.press('Tab');
  await expect(input).toHaveValue(String(value));
}
async function exportGraph(page: Page, maxElements: number): Promise<{ status: number; headers: ReturnType<typeof safeHeaders>; document: GraphExportDocument; downloaded: GraphExportDocument }> {
  await graphTab(page, 'Import / export');
  await fillNumber(surface(page).locator('.transfer-limit input'), maxElements);
  const button = surface(page).getByRole('button', { name: '下载 .graph.json', exact: true });
  await expect(button).toBeEnabled();
  const receivedResponse = page.waitForResponse(matches('GET', '/operations/export'), { timeout: responseTimeout });
  const receivedDownload = page.waitForEvent('download', { timeout: responseTimeout });
  await button.click();
  const response = await receivedResponse;
  expect(new URL(response.request().url()).searchParams.get('maxElements')).toBe(String(maxElements));
  const document = await actualJson<GraphExportDocument>(response, 200);
  const download = await receivedDownload;
  try {
    expect(await download.failure()).toBeNull();
    expect(download.suggestedFilename()).toBe(`${graph}.graph.json`);
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    const text = await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
    const downloaded = JSON.parse(text) as GraphExportDocument;
    expect(downloaded).toEqual(document);
    await expect(surface(page).getByRole('button', { name: '下载 .graph.json', exact: true })).not.toHaveClass(/n-button--loading/u);
    return { status: response.status(), headers: safeHeaders(response), document, downloaded };
  } finally { await download.delete(); }
}
function assertExport(document: GraphExportDocument, maxElements: number): void {
  assertSafeSequence(document.snapshotSequence);
  expect(document.snapshotSequence).toBe(seedTerminal.sequence);
  const vertices = seeded.vertices.slice(0, maxElements);
  const edges = seeded.edges.slice(0, Math.max(0, maxElements - vertices.length));
  expect(document.vertices).toEqual(vertices);
  expect(document.edges).toEqual(edges);
  expect(document.elementCount).toBe(vertices.length + edges.length);
  expect(document.elementCount).toBeLessThanOrEqual(maxElements);
  expect(document.truncated).toBe(maxElements < vertexCount + edgeCount);
}
function assertHistoryIdentity(history: HistoryEntry | undefined): void {
  expect(history).toMatchObject({ model: 'graph', database, target: graph, connectionId: profileId, connectionName: profileName });
  expect(history?.rowCount).toBeUndefined();
  expect(history?.recordsAffected).toBeUndefined();
  expect(history?.completeness).toBeUndefined();
}
function assertExportHistory(history: HistoryEntry | undefined, limit: number): void {
  assertHistoryIdentity(history);
  expect(history).toMatchObject({ title: 'Graph JSON export', action: 'graph_json_export', status: 'success', command: `maxElements=${limit}` });
  expect(history?.summary).toContain('文档完整性以 truncated 为准');
}
function assertOperationHistory(history: HistoryEntry | undefined, status: string, version: number): void {
  assertHistoryIdentity(history);
  expect(history).toMatchObject({ title: 'Graph 元素 Upsert', action: 'graph_元素_upsert', status,
    command: `upsert vertex ${editedId} expectedVersion=${version}` });
}
function assertSafeSequence(sequence: number): void { expect(Number.isSafeInteger(sequence) && sequence >= 0).toBe(true); }
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
  expect(evidence.requests.every((entry) => ['GET', 'PUT'].includes(entry.method))).toBe(true);
  expect(evidence.requests.filter((entry) => entry.method === 'PUT').every((entry) => entry.path === `${graphPath}/vertices/${editedId}`)).toBe(true);
}
async function historyEntries(page: Page): Promise<HistoryEntry[]> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] }).entries.slice(0, 64));
}
async function latestHistory(page: Page, title: string): Promise<HistoryEntry | undefined> {
  return (await historyEntries(page)).find((entry) => entry.title === title);
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `graph:${graph}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('graph-permission')).toBeVisible();
  await expect(surface(page).getByTestId('graph-beta')).toHaveText('Graph Beta');
  await expect(surface(page).locator('.graph-section')).toHaveCount(0);
  await expect(surface(page).locator('.graph-canvas canvas')).toHaveCount(0);
  await expect(surface(page).locator('.graph-inspector pre')).toHaveCount(0);
  await expect(surface(page).locator('.editor-form')).toHaveCount(0);
  await expect(surface(page).locator('.transfer-panel')).toHaveCount(0);
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page).locator('.graph-head__title-row')).not.toContainText('snapshot');
  await expect(surface(page)).not.toContainText(approvedValue);
  await expect(surface(page)).not.toContainText(deniedValue);
  await expect(surface(page)).not.toContainText(importDraftValue);
}
async function refreshRealSchema(page: Page): Promise<number> {
  const button = page.getByTitle('刷新资源', { exact: true });
  await expect(button).toBeEnabled();
  const schema = page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
  await button.click();
  const response = await schema;
  expect(response.status()).toBe(200);
  await expect(page.getByTitle('刷新资源', { exact: true }).locator('svg')).not.toHaveClass(/is-spinning/u);
  return response.status();
}
function safeHeaders(response: Response) {
  const headers = response.headers();
  return { contentType: headers['content-type'], contractVersion: headers['x-sonnetdb-contract-version'], requestId: headers['x-request-id'] };
}
async function actualJson<T>(response: Response, status: number): Promise<T> {
  expect(response.status()).toBe(status);
  const text = await response.text();
  if (Buffer.byteLength(text, 'utf8') > 262_144) throw new Error('Graph response JSON exceeded 256 KiB.');
  return JSON.parse(text) as T;
}
async function administratorGet(request: APIRequestContext, id: number): Promise<GraphVertex> {
  return apiJson<GraphVertex>(request, 'GET', `${graphPath}/vertices/${id}`, undefined, 200, administrator.token);
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    const text = await response.text();
    if (Buffer.byteLength(text, 'utf8') > 262_144) throw new Error('Graph control JSON exceeded 256 KiB.');
    return JSON.parse(text) as T;
  } finally { await response.dispose(); }
}
async function controlSql(request: APIRequestContext, sql: string): Promise<void> {
  const response = await request.post(new URL('/v1/sql', serverOrigin).href, { headers: { Authorization: `Bearer ${administrator.token}` },
    data: { sql, previewMaxRows: 200 }, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), 'The real administrator control SQL request must succeed.').toBe(200);
    expect(response.headers()['content-type']).toContain('ndjson');
    const content = await response.text();
    if (Buffer.byteLength(content, 'utf8') > 262_144) throw new Error('Control SQL exceeded 256 KiB.');
    const lines = content.trim().split(/\r?\n/u);
    if (lines.length > 1_024) throw new Error('Control SQL exceeded 1024 frames.');
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
  const configured = process.env.SONNETDB_GRAPH_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_GRAPH_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const expectedParent = resolve('D:/source/SonnetDB/artifacts/wb35-validation-20261007');
  if (!samePath(dirname(root), expectedParent) || !/^graph-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be a graph-real run immediately inside the named WB35 validation artifact directory.');
  }
  const parentInfo = await lstat(expectedParent);
  if (!parentInfo.isDirectory() || parentInfo.isSymbolicLink() || !samePath(await realpath(expectedParent), expectedParent)) {
    throw new Error('The evidence parent realpath escaped the named validation directory.');
  }
  const markerPath = join(root, 'run.json');
  const markerInfo = await lstat(markerPath);
  if (!markerInfo.isFile() || markerInfo.isSymbolicLink() || markerInfo.size > 65_536) throw new Error('The ordinary runner marker must fit 64 KiB.');
  const runInfo = await readFile(markerPath, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
  const marker = JSON.parse(runInfo) as { runId?: string; test?: string; baseUrl?: string };
  if (marker.runId !== basename(root) || marker.test !== 'graph-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('Graph evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, graph, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('Graph evidence exceeded 1 MiB/file or 8 MiB/run.');
  if ([password, administrator?.token, operator?.token, administrator?.tokenId, operator?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) {
    throw new Error('Credential material must never be written to Graph evidence.');
  }
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(content).digest('hex') });
}
