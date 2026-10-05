import { expect, test, type Page, type Route } from '@playwright/test';

// Real routed Vue/ECharts plus a Vite prop/program-entry harness. Auth and
// every API are fixtures. This is not a Server permission/engine budget,
// three-host, installation, AOT or release acceptance report.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const graph = 'PlantTopology:Original';
const secret = 'OldGraphPropertyMustDisappear';
const metadataSecret = 'OldGraphDiagnosticMustDisappear';
const importSecret = 'OldGraphImportDraftMustDisappear';
const writeDraft = 'OldGraphEditorDraftMustDisappear';
const maintenanceSecret = 'OldGraphMaintenancePrincipalMustDisappear';
const approvalId = '36600000-0000-0000-0000-000000000099';
const now = '2026-10-06T00:00:00.000Z';
const graphInfo = { name: graph, storageId: '36600000-0000-0000-0000-000000000001', recordFormatVersion: 1 };

type ReadAction = 'overview' | 'visualization' | 'element' | 'audit' | 'export';
interface ReadRequest { database: string; action: ReadAction; path: string; query: Record<string, string> }
interface WriteRequest { database: string; path: string; method: string; body: Record<string, unknown> }
interface Evidence { reads: ReadRequest[]; writes: WriteRequest[]; unexpected: string[] }
interface FixtureOptions {
  read?: (route: Route, request: ReadRequest) => Promise<void>;
  write?: (route: Route, request: WriteRequest) => Promise<void>;
}
type ProgramAction = 'refreshAll' | 'loadVisualization' | 'loadElement' | 'loadAudit' | 'exportGraph'
  | 'stageElementSave' | 'stageElementDelete' | 'stageImport' | 'stageMaintenanceRequest'
  | 'stageApprovalDecision' | 'rejectStagedApproval' | 'confirmApproval' | 'editSelectedElement';
interface CanvasSnapshot { vertices: string[]; edges: Array<{ source: string; target: string }>; snapshot: number | null; ownsCurrentDom: boolean }
interface Harness {
  setDatabase: (value: string) => Promise<void>;
  setGraph: (value: string) => Promise<void>;
  refreshGraphs: () => Promise<void>;
  setReadOnly: (value: boolean) => Promise<void>;
  setEditorId: (value: number) => Promise<void>;
  invoke: (action: ProgramAction) => Promise<void>;
  selectFirst: () => Promise<void>;
  seedImportFromLoadedVertices: () => Promise<void>;
  seedApprovalFromAudit: () => Promise<void>;
  importFixture: () => Promise<void>;
  canvasSnapshot: () => CanvasSnapshot;
  unmount: () => void;
}
type FixtureWindow = Window & { wb23GraphHarness: Harness; __wb23Exports?: string[] };

test.setTimeout(30_000);

test('Graph Beta routed original case/colon identity remains scoped to the database across same-name graphs', async ({ page }) => {
  const evidence = await prepare(page);
  await openGraph(page);
  await assertIdentity(page, east, 'normal');
  await expect(surface(page).getByTestId('graph-beta')).toContainText('Beta');
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await graphTab(page, 'Schema & diagnostics');
  await expect(surface(page)).toContainText('East diagnostic fixture');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await assertIdentity(page, west, 'normal');
  await graphTab(page, 'Schema & diagnostics');
  await expect(surface(page)).toContainText('West diagnostic fixture');
  await graphTab(page, 'Canvas');
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  expect(new URL(page.url()).searchParams.get('node')).toBe(graph);
  expect(evidence.reads.some((request) => request.database === east && request.action === 'visualization')).toBe(true);
  expect(evidence.reads.some((request) => request.database === west && request.action === 'visualization')).toBe(true);
  assertFixtureEvidence(evidence);
});

test('empty snapshot and ordinary visualization error remove old canvas content without exposing error bodies', async ({ page }) => {
  let state: 'normal' | 'empty' | 'error' = 'normal';
  const evidence = await prepare(page, { read: (route, request) => {
    if (state === 'empty' && request.action === 'overview') return json(route, { ...overview(request.database), vertexCount: 0, edgeCount: 0 });
    if (state === 'empty' && request.action === 'visualization') return json(route, { snapshotSequence: 42, vertices: [], edges: [], truncated: false });
    if (state === 'error' && request.action === 'visualization') return json(route, { message: secret }, 500);
    return defaultRead(route, request);
  } });
  await openGraph(page);
  await mountHarness(page);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await selectFirst(page);
  await expect(surface(page).locator('.graph-inspector pre')).toContainText(secret);
  state = 'empty';
  await program(page, 'refreshAll');
  await expect(surface(page)).toHaveAttribute('data-page-state', 'empty');
  expect((await canvasSnapshot(page)).vertices).toEqual([]);
  await expect(surface(page).locator('.graph-inspector pre')).toHaveCount(0);
  state = 'error';
  await program(page, 'loadVisualization');
  await expect(surface(page)).toHaveAttribute('data-page-state', 'error');
  expect((await canvasSnapshot(page)).vertices).toEqual([]);
  await expect(surface(page)).not.toContainText(secret);
  expect(JSON.stringify(await historyEntries(page))).not.toContain(secret);
  assertFixtureEvidence(evidence);
});

for (const status of [401, 403]) {
  test(`overview ${status} clears loaded canvas, metadata, editor/import/approval and latches through empty identities`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.action === 'overview'
      ? json(route, { message: secret, slowTraversals: [{ sql: metadataSecret }] }, status) : defaultRead(route, request, true) });
    await openGraph(page);
    await mountHarness(page);
    await primeSensitiveState(page);
    deny = true;
    await program(page, 'refreshAll');
    await assertPermissionHidden(page);
    const deniedReadCount = evidence.reads.length;
    await setDatabase(page, '');
    await expect(surface(page)).toHaveAttribute('data-database', '');
    await assertPermissionHidden(page);
    await setDatabase(page, east);
    await assertPermissionHidden(page);
    await setGraph(page, '');
    await assertPermissionHidden(page);
    await setGraph(page, graph);
    await assertPermissionHidden(page);
    await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.refreshGraphs());
    await program(page, 'refreshAll');
    await program(page, 'loadVisualization');
    await assertPermissionHidden(page);
    expect(evidence.reads).toHaveLength(deniedReadCount);
    assertFixtureEvidence(evidence);
  });
}

for (const [action, operation, status] of [
  ['visualization', 'loadVisualization', 403], ['element', 'loadElement', 401],
  ['audit', 'loadAudit', 403], ['export', 'exportGraph', 403],
] as const) {
  test(`${action} ${status} removes real previously loaded sensitive content and the pending element approval`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.action === action
      ? json(route, { message: secret, reason: maintenanceSecret }, status) : defaultRead(route, request, true),
      write: action === 'audit' ? (route, request) => request.path === '/maintenance/stage'
        ? json(route, maintenance(request.database), 202) : json(route, { code: 'wb23_fixture_write_forbidden' }, 501) : undefined });
    await openGraph(page);
    await mountHarness(page);
    await primeSensitiveState(page);
    if (action === 'audit') {
      await approval(page).getByTitle('返回编辑', { exact: true }).click();
      await graphTab(page, 'Maintenance');
      await surface(page).getByRole('button', { name: '预览并暂存', exact: true }).click();
      await confirmDangerApproval(page);
      await expect(surface(page).locator('.maintenance-approval')).toContainText(maintenanceSecret);
    }
    deny = true;
    await program(page, operation);
    await assertPermissionHidden(page);
    expect(evidence.reads.some((request) => request.action === action)).toBe(true);
    expect(JSON.stringify(await historyEntries(page))).not.toContain(secret);
    assertFixtureEvidence(evidence, action === 'audit' ? 1 : 0);
  });
}

test('explicit fixture element write 403 consumes approval and removes canvas, editor and import drafts', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => defaultRead(route, request, true),
    write: (route) => json(route, { message: secret }, 403) });
  await openGraph(page);
  await mountHarness(page);
  await primeSensitiveState(page);
  // This expressly dispatches only to an intercepted fixture mutation.
  await approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await assertPermissionHidden(page);
  expect(evidence.writes[0]).toMatchObject({ database: east, method: 'PUT', path: '/vertices/1' });
  assertFixtureEvidence(evidence, 1);
});

test('readonly Graph retains actual canvas, element reads and export while valid program write/import/maintenance entries remain gated', async ({ page }) => {
  const evidence = await prepare(page);
  await openGraph(page);
  await mountHarness(page);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await selectFirst(page);
  await program(page, 'editSelectedElement');
  await program(page, 'loadElement');
  await program(page, 'stageElementSave');
  await expect(approval(page)).toContainText('Graph 元素 Upsert');
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.setReadOnly(true));
  await assertIdentity(page, east, 'readonly');
  await expect(approval(page)).toHaveCount(0);
  await graphTab(page, 'Canvas');
  await assertRenderedCanvas(page);
  await expect.poll(async () => (await canvasSnapshot(page)).vertices.length, { timeout: 5_000 }).toBe(3);
  await graphTab(page, 'Restricted edit');
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.setEditorId(1));
  await program(page, 'loadElement');
  await expect(propertiesEditor(page)).toHaveValue(new RegExp(secret, 'u'));
  await expect(surface(page).getByRole('button', { name: '暂存 Upsert', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '暂存删除', exact: true })).toBeDisabled();
  await program(page, 'stageElementSave');
  await program(page, 'stageElementDelete');
  await graphTab(page, 'Import / export');
  await expect(surface(page).getByRole('button', { name: '选择 JSON', exact: true })).toBeDisabled();
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.importFixture());
  await expect(surface(page)).not.toContainText('ReadonlyFileMustNotPopulate.graph.json');
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.seedImportFromLoadedVertices());
  await program(page, 'stageImport');
  const exported = await exportGraph(page);
  expect(exported.vertices).toBeInstanceOf(Array);
  await graphTab(page, 'Maintenance');
  await program(page, 'loadAudit');
  // A valid record originates from the real component's already loaded audit
  // response. Hydration is an explicit program harness boundary, not a claim
  // that readonly Server principals can create or adopt an approval.
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.seedApprovalFromAudit());
  await expect(surface(page).getByRole('button', { name: '预览并暂存', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '批准并执行', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '拒绝', exact: true })).toBeDisabled();
  await program(page, 'stageMaintenanceRequest');
  await program(page, 'stageApprovalDecision');
  await program(page, 'rejectStagedApproval');
  await program(page, 'confirmApproval');
  await expect(approval(page)).toHaveCount(0);
  assertFixtureEvidence(evidence);
});

for (const capability of [undefined, false]) {
  test(`overview boundedVisualization=${String(capability)} blocks visualization requests and canvas through direct refreshes`, async ({ page }) => {
    const evidence = await prepare(page, { read: (route, request) => {
      if (request.action !== 'overview') return defaultRead(route, request);
      const body = overview(request.database);
      const capabilities: Partial<typeof body.capabilities> = { ...body.capabilities };
      if (capability === undefined) delete capabilities.boundedVisualization;
      else capabilities.boundedVisualization = capability;
      return json(route, { ...body, capabilities });
    } });
    await openGraph(page);
    await mountHarness(page);
    await expect(surface(page).getByTestId('graph-visualization-unavailable')).toBeVisible();
    await expect(surface(page).locator('.graph-canvas canvas')).toHaveCount(0);
    await program(page, 'loadVisualization');
    await program(page, 'refreshAll');
    await expect(surface(page).locator('.graph-canvas canvas')).toHaveCount(0);
    expect(evidence.reads.filter((request) => request.action === 'visualization')).toEqual([]);
    assertFixtureEvidence(evidence);
  });
}

test('client selected 10 and 1000 budgets cap total rendered elements and retain only edges with retained endpoints', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action !== 'visualization') return defaultRead(route, request);
    if (!['10', '1000'].includes(request.query.limit)) return json(route, { snapshotSequence: 41, truncated: true,
      vertices: [vertex(1), vertex(2), vertex(3)], edges: [edge(100, 1, 2), edge(101, 2, 3)] });
    const limit = Number(request.query.limit);
    const vertices = Array.from({ length: limit === 10 ? 8 : 600 }, (_, index) => vertex(index + 1, `Vertex:${index + 1}`));
    const edges = Array.from({ length: limit === 10 ? 9 : 600 }, (_, index) => edge(index + 100,
      index < 2 || limit === 1000 ? 1 : 9999, index < 2 || limit === 1000 ? 2 : 9998));
    return json(route, { snapshotSequence: 41, truncated: false, vertices, edges });
  } });
  await openGraph(page);
  await mountHarness(page);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  // Five retained elements fit the initial 250 budget; the Server's explicit
  // truncation still has to survive, independently of the client cap below.
  await expect(surface(page).locator('.graph-section--canvas .n-alert')).toContainText(/truncated|截断/u);
  await surface(page).locator('.limit-control input').fill('10');
  await program(page, 'loadVisualization');
  await expect(surface(page).locator('.graph-section--canvas .n-alert')).toContainText(/truncated|截断/u);
  await expect(surface(page).getByTestId('graph-preview-budget')).toContainText('10');
  const small = await canvasSnapshot(page);
  expect(small.vertices).toHaveLength(8);
  expect(small.edges).toHaveLength(2);
  expect(small.vertices.length + small.edges.length).toBeLessThanOrEqual(10);
  expect(small.edges.every((item) => small.vertices.includes(item.source) && small.vertices.includes(item.target))).toBe(true);
  await surface(page).locator('.limit-control input').fill('1000');
  await program(page, 'loadVisualization');
  const large = await canvasSnapshot(page);
  expect(large.vertices).toHaveLength(600);
  expect(large.edges).toHaveLength(400);
  expect(large.vertices.length + large.edges.length).toBe(1000);
  expect(large.edges.every((item) => large.vertices.includes(item.source) && large.vertices.includes(item.target))).toBe(true);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  expect(evidence.reads.filter((request) => request.action === 'visualization').some((request) => request.query.limit === '1000')).toBe(true);
  assertFixtureEvidence(evidence);
});

test('Inspector limits 32 properties/4096 characters while full loaded editor and JSON export retain original values', async ({ page }) => {
  const tail = 'LongGraphTailOnlyInFullEditorAndExport';
  const properties = Array.from({ length: 40 }, (_, index) => ({ propertyId: index + 1,
    value: { kind: 4, string: index === 0 ? `${'P'.repeat(8192)}${tail}` : `Property:${index}` } }));
  const fullVertex = { ...vertex(1, ''), properties };
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action === 'visualization') return json(route, { snapshotSequence: 41, truncated: false, vertices: [fullVertex], edges: [] });
    if (request.action === 'element') return json(route, fullVertex);
    if (request.action === 'export') return json(route, { snapshotSequence: 41, truncated: false, vertices: [fullVertex], edges: [], elementCount: 1 });
    return defaultRead(route, request);
  } });
  await openGraph(page);
  await mountHarness(page);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await selectFirst(page);
  await expect(surface(page).getByTestId('graph-property-budget')).toContainText(/32/u);
  await expect(surface(page).getByTestId('graph-property-budget')).toContainText(/4096|4,096/u);
  const preview = await surface(page).locator('.graph-inspector pre').innerText();
  expect(preview.length).toBeLessThanOrEqual(4096);
  expect(preview).not.toContain(tail);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  await program(page, 'editSelectedElement');
  expect(JSON.parse(await propertiesEditor(page).inputValue())).toEqual(properties);
  const exported = await exportGraph(page);
  expect((exported.vertices as Array<Record<string, unknown>>)[0].properties).toEqual(properties);
  assertFixtureEvidence(evidence);
});

test('newer visualization, database ABA and unmount invalidate late responses without resurrecting canvas or history', async ({ page }) => {
  let phase: 'idle' | 'newer' | 'aba' | 'unmount' = 'idle';
  const pending = new Set<string>();
  const completed = new Set<string>();
  const gates = { newer: boundedGate(), aba: boundedGate(), unmount: boundedGate() };
  const evidence = await prepare(page, { read: async (route, request) => {
    if (phase !== 'idle' && request.action === 'visualization' && request.database === east && !pending.has(phase)) {
      const captured = phase;
      pending.add(captured);
      await gates[captured].wait;
      try { await json(route, { snapshotSequence: 999, truncated: false, vertices: [vertex(999, 'StaleGraphMustNotRender')], edges: [] }); }
      catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
      finally { completed.add(captured); }
    } else await defaultRead(route, request);
  } });
  try {
    await openGraph(page);
    await mountHarness(page);
    await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
    phase = 'newer';
    await surface(page).getByRole('button', { name: '重新采样', exact: true }).click();
    await expect.poll(() => pending.has('newer'), { timeout: 5_000 }).toBe(true);
    await program(page, 'loadVisualization');
    gates.newer.release();
    await expect.poll(() => completed.has('newer'), { timeout: 5_000 }).toBe(true);
    expect((await canvasSnapshot(page)).vertices).not.toContain('999');
    phase = 'aba';
    await surface(page).getByRole('button', { name: '重新采样', exact: true }).click();
    await expect.poll(() => pending.has('aba'), { timeout: 5_000 }).toBe(true);
    await setDatabase(page, west);
    await setDatabase(page, east);
    await assertRenderedCanvas(page);
    await expect.poll(async () => (await canvasSnapshot(page)).snapshot, { timeout: 5_000 }).toBe(41);
    gates.aba.release();
    await expect.poll(() => completed.has('aba'), { timeout: 5_000 }).toBe(true);
    expect((await canvasSnapshot(page)).vertices).not.toContain('999');
    phase = 'unmount';
    await surface(page).getByRole('button', { name: '重新采样', exact: true }).click();
    await expect.poll(() => pending.has('unmount'), { timeout: 5_000 }).toBe(true);
    const historyCount = (await historyEntries(page)).length;
    await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.unmount());
    await expect(surface(page)).toHaveCount(0);
    gates.unmount.release();
    await expect.poll(() => completed.has('unmount'), { timeout: 5_000 }).toBe(true);
    await expect(page.locator('body')).not.toContainText('StaleGraphMustNotRender');
    expect(await historyEntries(page)).toHaveLength(historyCount);
    assertFixtureEvidence(evidence);
  } finally { gates.newer.release(); gates.aba.release(); gates.unmount.release(); }
});

test('maintenance staged is not completion and an unknown fixture approval consumes its plan without replay', async ({ page }) => {
  const evidence = await prepare(page, { write: (route, request) => request.path === '/maintenance/stage'
    ? json(route, maintenance(request.database, 'staged'), 202)
    : json(route, { message: 'Fixture response lost after approval dispatch' }, 503) });
  await openGraph(page);
  await mountHarness(page);
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await graphTab(page, 'Maintenance');
  await surface(page).getByRole('button', { name: '预览并暂存', exact: true }).click();
  await confirmDangerApproval(page);
  await expect(surface(page).locator('.maintenance-approval')).toContainText(approvalId);
  const stagedHistory = (await historyEntries(page)).filter((item) => item.title === '暂存 Graph 维护');
  expect(stagedHistory).toHaveLength(1);
  expect(stagedHistory[0].status).toBe('dry-run');
  expect(stagedHistory[0].summary).toMatch(/staged|尚未执行/u);
  await surface(page).getByRole('button', { name: '批准并执行', exact: true }).click();
  await confirmDangerApproval(page);
  await expect(approval(page)).toHaveCount(0);
  await expect.poll(async () => (await historyEntries(page)).find((item) => item.title === '批准 Graph 维护')?.status, { timeout: 5_000 }).toBe('unknown');
  await program(page, 'confirmApproval');
  await program(page, 'refreshAll');
  await expect(approval(page)).toHaveCount(0);
  expect(evidence.writes.map((request) => request.path)).toEqual(['/maintenance/stage', `/maintenance/${approvalId}/approve`]);
  assertFixtureEvidence(evidence, 2);
});

function surface(page: Page) { return page.getByTestId('workbench-graph'); }
function approval(page: Page) { return page.locator('.write-approval'); }
function propertiesEditor(page: Page) { return surface(page).locator('.editor-form label').filter({ hasText: 'Properties（typed JSON 数组）' }).locator('textarea'); }
async function openGraph(page: Page): Promise<void> {
  await page.goto(`/admin/app/sql?tool=graph&database=${encodeURIComponent(east)}&model=graph&node=${encodeURIComponent(graph)}`);
  await expect(surface(page)).toHaveAttribute('data-database', east);
}
async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `graph:${graph}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
}
async function graphTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name: new RegExp(`^${name.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&')}(?:\\s|$)`, 'u') }).click();
}
async function program(page: Page, action: ProgramAction): Promise<void> {
  await page.evaluate((action) => (window as FixtureWindow).wb23GraphHarness.invoke(action), action);
}
async function selectFirst(page: Page): Promise<void> {
  await page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.selectFirst());
}
async function setDatabase(page: Page, value: string): Promise<void> {
  await page.evaluate((value) => (window as FixtureWindow).wb23GraphHarness.setDatabase(value), value);
}
async function setGraph(page: Page, value: string): Promise<void> {
  await page.evaluate((value) => (window as FixtureWindow).wb23GraphHarness.setGraph(value), value);
}
async function canvasSnapshot(page: Page): Promise<CanvasSnapshot> {
  return page.evaluate(() => (window as FixtureWindow).wb23GraphHarness.canvasSnapshot());
}
async function assertRenderedCanvas(page: Page): Promise<void> {
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await expect.poll(async () => (await canvasSnapshot(page)).ownsCurrentDom, { timeout: 5_000 }).toBe(true);
}
async function primeSensitiveState(page: Page): Promise<void> {
  await expect(surface(page).locator('.graph-canvas canvas')).toBeVisible();
  await selectFirst(page);
  await expect(surface(page).locator('.graph-inspector pre')).toContainText(secret);
  await graphTab(page, 'Schema & diagnostics');
  await expect(surface(page)).toContainText(metadataSecret);
  await graphTab(page, 'Maintenance');
  await program(page, 'loadAudit');
  await expect(surface(page).locator('.audit-panel')).toContainText(maintenanceSecret);
  await graphTab(page, 'Import / export');
  const document = { vertices: [vertex(10, importSecret)], edges: [], truncated: false };
  await surface(page).locator('.transfer-panel textarea').fill(JSON.stringify(document));
  await graphTab(page, 'Canvas');
  await assertRenderedCanvas(page);
  await program(page, 'editSelectedElement');
  await program(page, 'loadElement');
  await expect(propertiesEditor(page)).toHaveValue(new RegExp(secret, 'u'));
  await propertiesEditor(page).fill(JSON.stringify([{ propertyId: 20, value: { kind: 4, string: writeDraft } }]));
  await surface(page).getByRole('button', { name: '暂存 Upsert', exact: true }).click();
  await expect(approval(page)).toContainText('Graph 元素 Upsert');
}
async function assertPermissionHidden(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('graph-permission')).toBeVisible();
  await expect(surface(page).locator('.graph-section')).toHaveCount(0);
  await expect(surface(page).locator('.graph-empty')).toHaveCount(0);
  await expect(surface(page).locator('.graph-canvas canvas')).toHaveCount(0);
  await expect(surface(page).locator('.graph-inspector pre')).toHaveCount(0);
  await expect(surface(page).locator('.editor-form')).toHaveCount(0);
  await expect(surface(page).locator('.transfer-panel')).toHaveCount(0);
  await expect(surface(page).locator('.maintenance-approval')).toHaveCount(0);
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page).locator('.graph-head__title-row')).not.toContainText('snapshot');
  await expect(surface(page)).not.toContainText(secret);
  await expect(surface(page)).not.toContainText(metadataSecret);
  await expect(surface(page)).not.toContainText(importSecret);
  await expect(surface(page)).not.toContainText(writeDraft);
  await expect(surface(page)).not.toContainText(maintenanceSecret);
}
async function confirmDangerApproval(page: Page): Promise<void> {
  await expect(approval(page)).toBeVisible();
  await approval(page).getByRole('checkbox').check();
  await approval(page).getByRole('button', { name: '确认执行 1 项高风险操作', exact: true }).click();
}
function boundedGate() {
  let release!: () => void;
  let released = false;
  const timer = setTimeout(() => release(), 10_000);
  const wait = new Promise<void>((resolve) => { release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } }; });
  return { wait, release };
}
async function mountHarness(page: Page): Promise<void> {
  const componentPath = '/src/components/GraphWorkbench.vue';
  const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`Graph harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'),
    pinia: dependency(authSource, 'pinia'), echarts: dependency(componentSource, 'echarts_core') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, graph, graphInfo }) => {
    const [vue, naive, piniaModule, echarts, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(modules.echarts), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb23-graph-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, graph, graphs: [graphInfo], readOnly: false });
    const componentRef = vue.ref(null);
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, { ...props, ref: componentRef }) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
    const setup = (): Record<string, any> => componentRef.value.$.setupState;
    const allowed: ProgramAction[] = ['refreshAll', 'loadVisualization', 'loadElement', 'loadAudit', 'exportGraph',
      'stageElementSave', 'stageElementDelete', 'stageImport', 'stageMaintenanceRequest', 'stageApprovalDecision',
      'rejectStagedApproval', 'confirmApproval', 'editSelectedElement'];
    (window as FixtureWindow).wb23GraphHarness = {
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); },
      setGraph: async (value) => { props.graph = value; props.graphs = value ? [{ ...graphInfo, name: value }] : []; await vue.nextTick(); },
      refreshGraphs: async () => { props.graphs = [graphInfo]; await vue.nextTick(); },
      setReadOnly: async (value) => { props.readOnly = value; await vue.nextTick(); },
      setEditorId: async (value) => { setup().editorKind = 'vertex'; setup().editorId = value; await vue.nextTick(); },
      invoke: async (action) => {
        if (!allowed.includes(action)) throw new Error('Graph action is outside the fixture allowlist.');
        const method = setup()[action];
        if (typeof method !== 'function') throw new Error(`Graph setup entry ${action} is unavailable.`);
        if (action === 'stageApprovalDecision') await method('approve');
        else await method();
        await vue.nextTick();
      },
      selectFirst: async () => {
        const vertex = setup().visualization?.vertices?.[0];
        if (!vertex) throw new Error('Graph fixture cannot select an unloaded vertex.');
        setup().selectedElement = { kind: 'vertex', data: vertex }; await vue.nextTick();
      },
      seedImportFromLoadedVertices: async () => {
        if (!setup().visualization?.vertices?.length) throw new Error('Graph import fixture requires loaded vertices.');
        setup().importText = JSON.stringify({ vertices: setup().visualization.vertices, edges: [], truncated: false }); await vue.nextTick();
      },
      seedApprovalFromAudit: async () => {
        const loaded = setup().audit?.find((item: { state: string }) => item.state === 'staged');
        if (!loaded) throw new Error('Graph approval fixture requires a loaded staged audit record.');
        setup().stagedApproval = { ...loaded }; await vue.nextTick();
      },
      importFixture: async () => {
        const file = new File(['{"vertices":[],"edges":[]}'], 'ReadonlyFileMustNotPopulate.graph.json', { type: 'application/json' });
        await setup().readImportFile({ target: { files: [file], value: '' } }); await vue.nextTick();
      },
      canvasSnapshot: () => {
        const target = host.querySelector('.graph-canvas');
        const instance = target ? echarts.getInstanceByDom(target) : undefined;
        const series = instance?.getOption()?.series?.[0];
        return { vertices: (series?.data ?? []).map((item: { id: string }) => String(item.id)),
          edges: (series?.edges ?? []).map((item: { source: string; target: string }) => ({ source: String(item.source), target: String(item.target) })),
          snapshot: setup().visualization?.snapshotSequence ?? null, ownsCurrentDom: Boolean(instance && instance.getDom() === target) };
      },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, graph, graphInfo });
}
async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  const fixtureOrigin = new URL(test.info().project.use.baseURL as string).origin;
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const fixture = window as FixtureWindow;
    fixture.__wb23Exports = [];
    const createUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => { void value.text().then((content) => fixture.__wb23Exports?.push(content)); return createUrl(value); }) as typeof URL.createObjectURL;
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {} removeEventListener(): void {} dispatchEvent(): boolean { return true; } close(): void {}
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database: east });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url());
    if (url.origin !== fixtureOrigin) {
      evidence.unexpected.push(`blocked external ${route.request().method()} ${url.origin}${url.pathname}`);
      return route.abort('blockedbyclient');
    }
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb23-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3') return json(route, []);
      if (suffix === '/graphs') return json(route, [graphInfo]);
      const prefix = `/graphs/${graph}`;
      if (suffix.startsWith(`${prefix}/`)) {
        const graphPath = suffix.slice(prefix.length);
        const method = route.request().method();
        if (method === 'GET') {
          const action: ReadAction | undefined = graphPath === '/operations/overview' ? 'overview'
            : graphPath === '/operations/visualization' ? 'visualization'
            : graphPath === '/operations/export' ? 'export'
            : graphPath === '/maintenance/audit' ? 'audit'
            : /^\/(?:vertices|edges)\/\d+$/u.test(graphPath) ? 'element' : undefined;
          if (action) {
            const request: ReadRequest = { database, action, path: graphPath, query: Object.fromEntries(url.searchParams) };
            evidence.reads.push(request);
            return options.read ? options.read(route, request) : defaultRead(route, request);
          }
        } else {
          const request: WriteRequest = { database, path: graphPath, method, body: route.request().postDataJSON() as Record<string, unknown> ?? {} };
          evidence.writes.push(request);
          return options.write ? options.write(route, request) : json(route, { code: 'wb23_fixture_write_forbidden' }, 501);
        }
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb23_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}
function vertex(id: number, value = secret) { return { id, elementVersion: 7, labels: [10], properties: [{ propertyId: 20, value: { kind: 4, string: value } }] }; }
function edge(id: number, sourceId: number, targetId: number) { return { id, elementVersion: 7, sourceId, targetId, labelId: 30, properties: [] }; }
function overview(database: string, sensitive = false) {
  return { graph: graphInfo, snapshotSequence: 41, vertexCount: 3, edgeCount: 2,
    labels: [{ labelId: 10, elementCount: 3 }], indexes: [{ elementType: 'vertex', labelId: 10, propertyId: 20, valueKind: 'string', entryCount: 3 }],
    degreeHistogram: [{ degree: 0, vertexCount: 1 }, { degree: 1, vertexCount: 2 }],
    slowTraversals: [{ timestampMs: Date.parse(now), fingerprint: 'graph-fixture', elapsedMs: 123.5, rowCount: 3, accessPath: 'adjacency_index', fallbackReason: null,
      sql: sensitive ? metadataSecret : `${database === east ? 'East' : 'West'} diagnostic fixture` }],
    slowTraversalSource: 'server_sql_diagnostics',
    capabilities: { schemaAndIndexes: true, degreeHistogram: true, slowTraversalDiagnostics: true, boundedVisualization: true,
      restrictedEditing: true, jsonImportExport: true, stagedMaintenance: true, audit: true } };
}
function maintenance(database: string, state = 'staged') {
  return { approvalId, occurredAtUtc: now, database, graph, action: 'RepairRebuild', state, principal: maintenanceSecret,
    expiresAtUtc: '2099-10-06T00:00:00.000Z', compactOnCompletion: false, maxWorkUnits: 64 };
}
async function defaultRead(route: Route, request: ReadRequest, sensitive = false): Promise<void> {
  if (request.action === 'overview') return json(route, overview(request.database, sensitive));
  const vertices = [vertex(1), vertex(2, 'Other graph vertex'), vertex(3, 'Final graph vertex')];
  const edges = [edge(100, 1, 2), edge(101, 2, 3)];
  if (request.action === 'visualization') return json(route, { snapshotSequence: 41, truncated: false, vertices, edges });
  if (request.action === 'element') return json(route, request.path.startsWith('/edges/') ? edges[0] : vertices[0]);
  if (request.action === 'audit') return json(route, { items: [maintenance(request.database)] });
  return json(route, { snapshotSequence: 41, truncated: false, vertices, edges, elementCount: 5 });
}
async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}
function assertFixtureEvidence(evidence: Evidence, expectedWrites = 0): void {
  expect(evidence.writes).toHaveLength(expectedWrites);
  expect(evidence.unexpected).toEqual([]);
}
async function historyEntries(page: Page): Promise<Array<Record<string, unknown>>> {
  return page.evaluate(() => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries);
}
async function exportGraph(page: Page): Promise<Record<string, unknown>> {
  const before = await page.evaluate(() => (window as FixtureWindow).__wb23Exports?.length ?? 0);
  await program(page, 'exportGraph');
  await expect.poll(() => page.evaluate(() => (window as FixtureWindow).__wb23Exports?.length ?? 0), { timeout: 5_000 }).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as FixtureWindow).__wb23Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('Graph export exceeded the fixture read budget.');
  return JSON.parse(content) as Record<string, unknown>;
}
