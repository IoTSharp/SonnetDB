import { readFile, stat } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { expect, test, type Locator, type Page, type Route } from '@playwright/test';

// Actual routed Vue and SQL helpers with fully intercepted fixture APIs. The
// explicitly named Vite prop harness exercises host readonly/context signals;
// neither mode is real Server authorization, OS dialogs or three-host evidence.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const measurementName = 'DeviceMeasures_Original';
const secondName = 'SecondaryMeasures_Original';
const secret = 'MeasurementOldPayloadMustDisappear';
const draft = 'MeasurementOldDraftMustDisappear';
const columnNames = ['time', 'DeviceID', 'Payload_Original', 'Temperature'];
const measurement = { name: measurementName, columns: [
  { name: 'time', role: 'time', dataType: 'TIMESTAMP' },
  { name: 'DeviceID', role: 'tag', dataType: 'STRING' },
  { name: 'Payload_Original', role: 'field', dataType: 'STRING' },
  { name: 'Temperature', role: 'field', dataType: 'DOUBLE' },
] };
const measurements = [measurement, { ...measurement, name: secondName }];
const tables = [{ name: 'MonitorTable_Original', columns: [{ name: 'id', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 }], primaryKey: ['id'], indexes: [], foreignKeys: [], createdUtc: '2026-10-06T00:00:00Z' }];

interface SqlRead { database: string; sql: string; body: Record<string, unknown>; kind: 'point' | 'monitor' }
interface SqlWrite { database: string; body: { statements: Array<{ sql: string; parameters?: unknown }> } }
interface Evidence { reads: SqlRead[]; writes: SqlWrite[]; unexpected: string[] }
interface Options { read?: (route: Route, request: SqlRead) => Promise<void>; write?: (route: Route, request: SqlWrite) => Promise<void> }
interface Harness { context: (db: string, empty?: boolean) => Promise<void>; refreshSchema: () => Promise<void>; refreshAuth: () => Promise<void>; readonly: (value: boolean) => Promise<void>; unmount: () => void }
type FixtureWindow = Window & { wb30Harness: Harness; wb30FileGate?: { release: () => void; completed: boolean } };

test.describe.configure({ retries: 0 });
test.use({ actionTimeout: 5_000 });
test.setTimeout(30_000);

test('routed Measurement preserves original database/resource names and distinguishes normal, empty and sanitized error', async ({ page }) => {
  let mode = 'normal';
  const evidence = await prepare(page, { read: (route, request) => mode === 'empty' ? rows(route, [])
    : mode === 'error' ? json(route, { code: 'storage_failure', message: secret }, 500) : defaultRead(route, request) });
  await openMeasurement(page);
  await expect(surface(page)).toHaveAttribute('data-state', 'normal');
  await expect(surface(page)).toContainText('East:OriginalPayload');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await expect(surface(page)).toHaveAttribute('data-database', west);
  await expect(surface(page)).toContainText('West:OriginalPayload');
  expect(new URL(page.url()).searchParams.get('node')).toBe(measurementName);
  mode = 'empty';
  await query(page);
  await expect(surface(page)).toHaveAttribute('data-state', 'empty');
  mode = 'error';
  await query(page);
  await expect(surface(page)).toHaveAttribute('data-state', 'error');
  await expect(surface(page)).not.toContainText(secret);
  assertEvidence(evidence);
});

for (const status of [401, 403]) {
  test(`point fixture HTTP${status} removes editor/import/Schema/monitor payload and same-identity refresh cannot unlock`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.kind === 'point'
      ? json(route, { code: status === 401 ? 'unauthorized' : 'forbidden', message: secret }, status) : defaultRead(route, request, secret) });
    await openMeasurement(page);
    await mountHarness(page);
    await expect(surface(page)).toContainText(secret);
    await fillImport(page, 1, draft);
    await tab(page, '数据点');
    await fillPointEditor(page, draft);
    deny = true;
    await query(page);
    await hiddenPermission(page);
    const before = evidence.reads.length;
    await harness(page, 'refreshSchema');
    await harness(page, 'refreshAuth');
    await harness(page, 'context', '', true);
    await hiddenPermission(page, '');
    await harness(page, 'context', east);
    await hiddenPermission(page);
    await tab(page, '文件导入');
    await hiddenPermission(page);
    await tab(page, 'Schema');
    await hiddenPermission(page);
    expect(evidence.reads).toHaveLength(before);
    assertEvidence(evidence);
  });
}

for (const status of [401, 403]) {
  test(`monitor fixture HTTP${status} locks all data planes and clears earlier point/editor/import payload`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.kind === 'monitor'
      ? json(route, { code: status === 401 ? 'unauthorized' : 'forbidden', message: secret }, status) : defaultRead(route, request, secret) });
    await openMeasurement(page);
    await fillImport(page, 1, draft);
    await tab(page, '数据点');
    await fillPointEditor(page, draft);
    await tab(page, '实时监控');
    await refreshMonitor(page);
    await expect(surface(page).locator('.measurement-monitor')).toContainText(secret);
    deny = true;
    await refreshMonitor(page);
    await hiddenPermission(page);
    await tab(page, '数据点');
    await hiddenPermission(page);
    await expect(surface(page).getByRole('button', { name: '查询', exact: true })).toBeDisabled();
    assertEvidence(evidence);
  });
}

test('write fixture403 consumes normal point approval, clears all old payload and never replays after auth/Schema refresh', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => defaultRead(route, request, secret),
    write: (route) => json(route, { code: 'forbidden', message: secret }, 403) });
  await openMeasurement(page);
  await mountHarness(page);
  await fillImport(page, 1, draft);
  await tab(page, '数据点');
  await fillPointEditor(page, draft);
  await surface(page).getByRole('button', { name: '暂存新增', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Measurement point changes' });
  await expect(approval).toContainText(`${east}.${measurementName}`);
  await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await hiddenPermission(page);
  const item = await latestHistory(page, 'edit');
  expect(item).toMatchObject({ status: 'error', database: east, target: measurementName, recordsAffected: 0 });
  await harness(page, 'refreshSchema');
  await harness(page, 'refreshAuth');
  await hiddenPermission(page);
  expect(evidence.writes).toHaveLength(1);
  assertEvidence(evidence, 1);
});

test('readonly Vite prop host preserves actual querying/export while point staging, import and file handlers remain forbidden', async ({ page }) => {
  const evidence = await prepare(page);
  await openMeasurement(page);
  await mountHarness(page, true);
  await expect(surface(page)).toHaveAttribute('data-state', 'readonly');
  await expect(surface(page).getByRole('button', { name: '新增数据点', exact: true })).toBeDisabled();
  await query(page);
  const exported = await exportJson(page);
  expect(exported).toHaveLength(1);
  await tab(page, '文件导入');
  await expect(importInput(page)).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: '选择文件', exact: true })).toBeDisabled();
  await surface(page).locator('.measurement-file-input').setInputFiles({ name: 'readonly.csv', mimeType: 'text/csv', buffer: Buffer.from(csv(1, draft)) });
  await expect(importInput(page)).toHaveValue('');
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await tab(page, 'Schema');
  await expect(surface(page).locator('.measurement-schema')).toContainText('Payload_Original');
  assertEvidence(evidence);
});

test('point responses across same-name database ABA and unmount cannot restore the old payload', async ({ page }) => {
  let delay = false; let started = 0; let finished = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (delay && request.kind === 'point' && request.database === east) {
      started += 1; await gate.wait;
      try { await defaultRead(route, request, secret); } finally { finished += 1; }
    } else await defaultRead(route, request);
  } });
  try {
    await openMeasurement(page); await mountHarness(page);
    delay = true;
    await surface(page).getByRole('button', { name: '查询', exact: true }).click();
    await boundedUntil(() => started === 1);
    delay = false;
    await harness(page, 'context', west);
    await expect(surface(page)).toContainText('West:OriginalPayload');
    await harness(page, 'context', east);
    await expect(surface(page)).toContainText('East:OriginalPayload');
    gate.release(); await boundedUntil(() => finished === 1);
    await expect(surface(page)).not.toContainText(secret);
    await harness(page, 'unmount');
    await expect(surface(page)).toHaveCount(0);
    assertEvidence(evidence);
  } finally { gate.release(); }
});

test('same-token auth and same-name Schema signals invalidate delayed point response and maintain the new loaded payload', async ({ page }) => {
  let delay = false; let started = 0; let finished = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (delay && request.kind === 'point') {
      started += 1; await gate.wait;
      try { await defaultRead(route, request, secret); } finally { finished += 1; }
    } else await defaultRead(route, request);
  } });
  try {
    await openMeasurement(page); await mountHarness(page);
    delay = true; await surface(page).getByRole('button', { name: '查询', exact: true }).click();
    await boundedUntil(() => started === 1);
    delay = false; await harness(page, 'refreshAuth'); await harness(page, 'refreshSchema');
    await expect(surface(page)).toContainText('East:OriginalPayload');
    gate.release(); await boundedUntil(() => finished === 1);
    await expect(surface(page)).not.toContainText(secret);
    assertEvidence(evidence);
  } finally { gate.release(); }
});

test('an old point finally cannot remove a newer point query busy state', async ({ page }) => {
  let delay = false; let pending = 0; let done = 0;
  const old = boundedGate(); const fresh = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (!delay || request.kind !== 'point') return defaultRead(route, request);
    const position = ++pending; await (position === 1 ? old.wait : fresh.wait);
    try { await defaultRead(route, request, position === 1 ? secret : 'NewPointPayload'); } finally { done += 1; }
  } });
  try {
    await openMeasurement(page); await mountHarness(page); delay = true;
    const queryButton = surface(page).getByRole('button', { name: /^(?:loading\s+)?查询$/u });
    await queryButton.click(); await boundedUntil(() => pending === 1);
    // A real same-name host Schema signal starts the replacement read; no
    // disabled button, modal or private component draft is bypassed.
    await harness(page, 'refreshSchema'); await boundedUntil(() => pending === 2);
    old.release(); await boundedUntil(() => done === 1);
    await expect(queryButton).toHaveClass(/n-button--loading/u);
    fresh.release(); await boundedUntil(() => done === 2);
    await expect(queryButton).not.toHaveClass(/n-button--loading/u);
    await expect(surface(page)).toContainText('NewPointPayload');
    await expect(surface(page)).not.toContainText(secret);
    assertEvidence(evidence);
  } finally { old.release(); fresh.release(); }
});

test('monitor target and limit changes isolate old response and old finally from the new busy target', async ({ page }) => {
  let delay = false; let pending = 0; let done = 0;
  const old = boundedGate(); const fresh = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (!delay || request.kind !== 'monitor') return defaultRead(route, request);
    const position = ++pending; await (position === 1 ? old.wait : fresh.wait);
    try { await defaultRead(route, request, position === 1 ? secret : 'NewMonitorTargetPayload'); } finally { done += 1; }
  } });
  try {
    await openMeasurement(page); await tab(page, '实时监控');
    await choose(page, surface(page).locator('.monitor-controls__interval').first(), '30 秒');
    delay = true;
    await surface(page).getByRole('button', { name: '开始', exact: true }).click();
    await boundedUntil(() => pending === 1);
    await choose(page, surface(page).locator('.monitor-controls__target'), secondName);
    await boundedUntil(() => pending >= 2);
    old.release(); await boundedUntil(() => done === 1);
    await expect(surface(page).getByRole('button', { name: /^(?:loading\s+)?立即刷新$/u })).toHaveClass(/n-button--loading/u);
    fresh.release(); await boundedUntil(() => done >= 2);
    await expect(surface(page)).toContainText('NewMonitorTargetPayload');
    await expect(surface(page)).not.toContainText(secret);
    delay = false;
    await choose(page, surface(page).locator('.monitor-controls__interval').last(), '500 行');
    await refreshMonitor(page);
    expect(evidence.reads.at(-1)?.sql).toContain(secondName);
    expect(evidence.reads.at(-1)?.sql).toContain('LIMIT 500');
    await surface(page).getByRole('button', { name: '暂停', exact: true }).click();
    assertEvidence(evidence);
  } finally { old.release(); fresh.release(); }
});

for (const invalidation of ['context', 'readonly'] as const) {
  test(`late File.text ${invalidation} cannot refill import content or create an approval in the new context`, async ({ page }) => {
    const evidence = await prepare(page);
    await openMeasurement(page); await mountHarness(page); await tab(page, '文件导入');
    await installDelayedFile(page);
    await surface(page).locator('.measurement-file-input').setInputFiles({ name: 'wb30-late.csv', mimeType: 'text/csv', buffer: Buffer.from(csv(1, draft)) });
    if (invalidation === 'context') await harness(page, 'context', west);
    else await harness(page, 'readonly', undefined, true);
    await page.evaluate(() => (window as FixtureWindow).wb30FileGate?.release());
    await boundedUntil(() => page.evaluate(() => Boolean((window as FixtureWindow).wb30FileGate?.completed)));
    await expect(importInput(page)).toHaveValue('');
    await expect(surface(page).locator('.measurement-import-grid')).toHaveCount(0);
    await expect(page.getByRole('dialog')).toHaveCount(0);
    assertEvidence(evidence);
  });
}

test('already dispatched write switches database to unknown original history and consumed approval never returns', async ({ page }) => {
  let sent = 0; let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { write: async (route, request) => {
    sent += 1; await gate.wait;
    try { await batch(route, request); } finally { completed += 1; }
  } });
  try {
    await openMeasurement(page); await mountHarness(page); await fillPointEditor(page, draft);
    await surface(page).getByRole('button', { name: '暂存新增', exact: true }).click();
    await page.getByRole('dialog', { name: 'Measurement point changes' }).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
    await boundedUntil(() => sent === 1);
    await harness(page, 'context', west);
    gate.release(); await boundedUntil(() => completed === 1);
    await boundedUntil(async () => (await latestHistory(page, 'edit'))?.status === 'unknown');
    expect(await latestHistory(page, 'edit')).toMatchObject({ status: 'unknown', database: east, target: measurementName, connectionId: 'wb30-fixture' });
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await harness(page, 'context', east);
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(surface(page).locator('.point-editor')).toHaveCount(0);
    assertEvidence(evidence, 1);
  } finally { gate.release(); }
});

test('501 returned point rows are capped to500 before visible mapping and current JSON export', async ({ page }) => {
  let overflow = false;
  const evidence = await prepare(page, { read: (route, request) => request.kind === 'point' && overflow ? rows(route, fixtureRows(501)) : defaultRead(route, request) });
  await openMeasurement(page);
  await choose(page, surface(page).locator('.measurement-filterbar__limit .n-select'), '500 行');
  overflow = true; await query(page);
  await expect(surface(page)).toHaveAttribute('data-state', 'longContent');
  await expect(surface(page).locator('.measurement-statusbar')).toContainText('500');
  const exported = await exportJson(page);
  expect(exported).toHaveLength(500);
  expect(exported.at(-1)).toMatchObject({ Payload_Original: 'BudgetPayload:0499' });
  expect(JSON.stringify(exported)).not.toContain('BudgetPayload:0500');
  expect(evidence.reads.at(-1)?.body.previewMaxRows).toBe(500);
  const pointGrid = surface(page).locator('.measurement-grid');
  const renderedRows = await pointGrid.locator('tbody tr').count();
  expect(renderedRows).toBeGreaterThan(0);
  expect(renderedRows).toBeLessThan(100);
  await pointGrid.locator('.v-vl').evaluate((element) => { element.scrollTop = element.scrollHeight; });
  await expect(pointGrid.getByText('BudgetPayload:0499', { exact: true })).toBeVisible();
  await expect(pointGrid).not.toContainText('BudgetPayload:0500');
  assertEvidence(evidence);
});

test('501 returned monitor rows are capped to500 before chart/grid with the original monitor target preserved', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => request.kind === 'monitor' ? rows(route, fixtureRows(501)) : defaultRead(route, request) });
  await openMeasurement(page); await tab(page, '实时监控');
  await choose(page, surface(page).locator('.monitor-controls__interval').last(), '500 行');
  await refreshMonitor(page);
  await expect(surface(page).locator('.monitor-stats')).toContainText('500');
  await expect(surface(page).locator('.monitor-stats > div').filter({ has: page.getByText('返回行', { exact: true }) }).locator('strong')).toHaveText('500');
  const monitorGrid = surface(page).locator('.monitor-grid-panel');
  const renderedRows = await monitorGrid.locator('tbody tr').count();
  expect(renderedRows).toBeGreaterThan(0);
  expect(renderedRows).toBeLessThan(100);
  await monitorGrid.locator('.v-vl').evaluate((element) => { element.scrollTop = element.scrollHeight; });
  await expect(monitorGrid.getByText('BudgetPayload:0499', { exact: true })).toBeVisible();
  await expect(surface(page)).not.toContainText('BudgetPayload:0500');
  const chartPath = await surface(page).getByRole('img', { name: 'SQL 结果折线图' }).locator('path').first().getAttribute('d');
  expect(chartPath?.match(/[ML]/gu)).toHaveLength(500);
  expect(evidence.reads.at(-1)?.body.previewMaxRows).toBe(500);
  expect(evidence.reads.at(-1)?.sql).toContain(measurementName);
  assertEvidence(evidence);
});

test('normal101-row import uses complete per-statement fixture terminals for100 then1 and consumes one approval', async ({ page }) => {
  const evidence = await prepare(page);
  await openMeasurement(page); await fillImport(page, 101);
  await surface(page).getByRole('button', { name: '暂存导入', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'Measurement import' });
  await expect(approval).toContainText('INSERT 101 POINTS');
  await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await boundedUntil(() => evidence.writes.length === 2);
  await expect(approval).toHaveCount(0);
  expect(evidence.writes.map((request) => request.body.statements.length)).toEqual([100, 1]);
  expect(await latestHistory(page, 'import')).toMatchObject({ status: 'success', database: east, target: measurementName, recordsAffected: 101 });
  assertEvidence(evidence, 2);
});

test('normal stop after first100 confirmed import points preserves progress and requires a fresh approval for remaining1', async ({ page }) => {
  let completed = 0;
  const gate = boundedGate();
  const evidence = await prepare(page, { write: async (route, request) => {
    if (request.body.statements.length === 100) await gate.wait;
    try { await batch(route, request); } finally { completed += 1; }
  } });
  try {
    await openMeasurement(page); await fillImport(page, 101);
    await surface(page).getByRole('button', { name: '暂存导入', exact: true }).click();
    let approval = page.getByRole('dialog', { name: 'Measurement import' });
    await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
    await boundedUntil(() => evidence.writes.length === 1);
    await approval.getByRole('button', { name: '停止后续批次', exact: true }).click();
    gate.release(); await boundedUntil(() => completed === 1);
    await boundedUntil(async () => (await latestHistory(page, 'import'))?.recordsAffected === 100);
    expect(evidence.writes).toHaveLength(1);
    approval = page.getByRole('dialog', { name: 'Measurement import' });
    await expect(approval).toContainText('INSERT 1 POINTS');
    await approval.getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
    await boundedUntil(() => completed === 2);
    await expect(approval).toHaveCount(0);
    expect(evidence.writes.map((request) => request.body.statements.length)).toEqual([100, 1]);
    assertEvidence(evidence, 2);
  } finally { gate.release(); }
});

test('monitor timer makes at most12 sequential fixture refreshes and stops without installing unbounded polling', async ({ page }) => {
  const evidence = await prepare(page);
  await openMeasurement(page); await tab(page, '实时监控');
  await page.clock.install();
  await surface(page).getByRole('button', { name: '开始', exact: true }).click();
  await boundedUntil(() => evidence.reads.filter((item) => item.kind === 'monitor').length === 1);
  // Fixed12 ticks, at most5s each under browser clock; process wall deadline30s.
  // runFor visits interval callbacks, unlike fastForward's skipped callbacks.
  const deadline = Date.now() + 20_000;
  for (let round = 0; round < 12 && Date.now() < deadline; round += 1) {
    await page.clock.runFor(2_000);
    await boundedUntil(async () => !(await surface(page).getByRole('button', { name: '立即刷新', exact: true }).evaluate((node) => node.classList.contains('n-button--loading'))));
  }
  expect(evidence.reads.filter((item) => item.kind === 'monitor')).toHaveLength(12);
  await expect(surface(page).getByRole('button', { name: '开始', exact: true })).toBeVisible();
  await page.clock.runFor(2_000);
  expect(evidence.reads.filter((item) => item.kind === 'monitor')).toHaveLength(12);
  assertEvidence(evidence);
});

test('an exact SQL forbidden terminal at HTTP200 also locks and does not expose its server message', async ({ page }) => {
  let deny = false;
  const evidence = await prepare(page, { read: (route, request) => deny
    ? safeFulfill(route, { status: 200, contentType: 'application/x-ndjson', body: JSON.stringify({ type: 'error', code: 'forbidden', message: secret }) })
    : defaultRead(route, request) });
  await openMeasurement(page); deny = true; await query(page);
  await hiddenPermission(page); assertEvidence(evidence);
});

function surface(page: Page) { return page.getByTestId('workbench-measurement'); }
function importInput(page: Page) { return surface(page).getByPlaceholder('粘贴 CSV、JSON 数组或 JSONL 数据', { exact: true }); }
async function tab(page: Page, name: string) { await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click(); }
async function choose(page: Page, select: Locator, option: string) { await select.click(); await page.locator('.n-base-select-option').getByText(option, { exact: true }).click(); }
async function openMeasurement(page: Page) {
  await page.goto(`/admin/app/sql?${new URLSearchParams({ tool: 'measurement', database: east, model: 'measurement', node: measurementName })}`);
  await expect(surface(page)).toHaveAttribute('data-database', east);
  await expect(surface(page)).toHaveAttribute('data-resource-key', measurementName);
  await expect(surface(page).locator('.measurement-grid .n-data-table-td').first()).toBeVisible();
}
async function query(page: Page) {
  await surface(page).getByRole('button', { name: '查询', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: '查询', exact: true })).not.toHaveClass(/n-button--loading/u);
}
async function refreshMonitor(page: Page) {
  await surface(page).getByRole('button', { name: '立即刷新', exact: true }).click();
  await expect(surface(page).getByRole('button', { name: '立即刷新', exact: true })).not.toHaveClass(/n-button--loading/u);
}
async function fillPointEditor(page: Page, payload: string) {
  await surface(page).getByRole('button', { name: '新增数据点', exact: true }).click();
  await surface(page).locator('.point-field').filter({ hasText: /^DeviceID/u }).locator('input').fill('Draft:OriginalDevice');
  await surface(page).locator('.point-field').filter({ hasText: /^Payload_Original/u }).locator('input').fill(payload);
  await surface(page).locator('.point-field').filter({ hasText: /^Temperature/u }).locator('input').fill('23.5');
}
function csv(count: number, payload = 'ImportedOriginalPayload') {
  return ['time,DeviceID,Payload_Original,Temperature', ...Array.from({ length: count }, (_, position) => `${1780000000000 + position},Import:Original:${position},${payload},23.5`)].join('\n');
}
async function fillImport(page: Page, count: number, payload?: string) {
  await tab(page, '文件导入'); await importInput(page).fill(csv(count, payload));
  await surface(page).getByRole('button', { name: '解析', exact: true }).click();
  await expect(surface(page)).toContainText(`${count} 行 · 4 个源列`);
}
async function hiddenPermission(page: Page, database = east) {
  await expect(surface(page)).toHaveAttribute('data-state', 'permission');
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page).getByTestId('measurement-permission-lock')).toBeVisible();
  await expect(surface(page).locator('.point-editor')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-grid .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.monitor-grid-panel .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-schema .n-data-table-td')).toHaveCount(0);
  await expect(surface(page).locator('.measurement-import-grid')).toHaveCount(0);
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(surface(page)).not.toContainText(secret);
  await expect(surface(page)).not.toContainText(draft);
  if (await importInput(page).count()) await expect(importInput(page)).toHaveValue('');
}
function boundedGate() {
  let release!: () => void; let released = false;
  const wait = new Promise<void>((resolve) => { release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } }; });
  const timer = setTimeout(() => release(), 10_000);
  return { wait, release };
}
async function boundedUntil(condition: () => boolean | Promise<boolean>) {
  const deadline = Date.now() + 5_000;
  for (let attempt = 0; attempt < 40 && Date.now() < deadline; attempt += 1) {
    if (await condition()) return;
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error('Fixture condition did not complete within40 attempts/5s.');
}
async function latestHistory(page: Page, action: string): Promise<Record<string, unknown> | undefined> {
  return page.evaluate((name) => (JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: Array<Record<string, unknown>> }).entries.slice(0, 64).find((entry) => entry.action === name), action);
}
function fixtureRows(count: number) { return Array.from({ length: count }, (_, position) => [1780000000000 + position, `BudgetDevice:${position}`, `BudgetPayload:${String(position).padStart(4, '0')}`, 23.5 + position]); }
async function rows(route: Route, values: unknown[][]) {
  await safeFulfill(route, { status: 200, contentType: 'application/x-ndjson', body: [JSON.stringify({ type: 'meta', columns: columnNames }),
    ...values.map((row) => JSON.stringify(row)), JSON.stringify({ type: 'end', rowCount: values.length, recordsAffected: -1, elapsedMs: 1.2 })].join('\n') });
}
async function defaultRead(route: Route, request: SqlRead, payload?: string) { await rows(route, [[1780000000000, `${request.database === east ? 'East' : 'West'}:OriginalDevice`, payload ?? `${request.database === east ? 'East' : 'West'}:OriginalPayload`, 23.5]]); }
async function batch(route: Route, request: SqlWrite) {
  await safeFulfill(route, { status: 200, contentType: 'application/x-ndjson', body: request.body.statements.map(() => JSON.stringify({ type: 'end', rowCount: 0, recordsAffected: 1, elapsedMs: 1.2 })).join('\n') });
}
async function json(route: Route, body: unknown, status = 200) { await safeFulfill(route, { status, contentType: 'application/json', body: JSON.stringify(body) }); }
async function safeFulfill(route: Route, options: Parameters<Route['fulfill']>[0]) {
  try { await route.fulfill(options); }
  catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
}
function assertEvidence(evidence: Evidence, writes = 0) { expect(evidence.writes).toHaveLength(writes); expect(evidence.unexpected).toEqual([]); }
async function prepare(page: Page, options: Options = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  await page.addInitScript(({ db }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'wb30-fixture', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'wb30-fixture', name: 'WB30 fixture', kind: 'managed-local', baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'wb30-fixture', activeDatabase: db }));
    class QuietEventSource {
      static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2;
      readonly readyState = 1; url = ''; withCredentials = false;
      onopen: ((event: Event) => void) | null = null; onmessage: ((event: MessageEvent) => void) | null = null; onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {} removeEventListener(): void {} dispatchEvent(): boolean { return true; } close(): void {}
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { db: east });
  await page.route('**/*', async (route) => {
    const path = decodeURIComponent(new URL(route.request().url()).pathname);
    if (!path.startsWith('/v1/') && !path.startsWith('/healthz')) return route.continue();
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb30-fixture', organization: 'Fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const parsed = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (parsed && [east, west].includes(parsed[1])) {
      const db = parsed[1]; const suffix = parsed[2];
      if (suffix === '/schema') return json(route, { measurements, tables, documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      if (suffix === '/sql') {
        const body = route.request().postDataJSON() as Record<string, unknown>;
        const sql = String(body.sql ?? '');
        const request: SqlRead = { database: db, sql, body, kind: /^SELECT\s+\*/iu.test(sql) ? 'monitor' : 'point' };
        if (evidence.reads.length >= 96) throw new Error('Fixture read request budget96 exceeded.');
        evidence.reads.push(request);
        return options.read ? options.read(route, request) : defaultRead(route, request);
      }
      if (suffix === '/sql/batch') {
        const request: SqlWrite = { database: db, body: route.request().postDataJSON() as SqlWrite['body'] };
        if (evidence.writes.length >= 10 || request.body.statements.length > 1000) throw new Error('Fixture write request budget exceeded.');
        evidence.writes.push(request);
        return options.write ? options.write(route, request) : batch(route, request);
      }
    }
    if (evidence.unexpected.length < 32) evidence.unexpected.push(`${route.request().method()} ${path}`);
    return json(route, { code: 'wb30_fixture_not_mocked', message: path }, 501);
  });
  return evidence;
}
async function mountHarness(page: Page, readonly = false) {
  const componentPath = '/src/components/MeasurementWorkbench.vue'; const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()), page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text())]);
  function dependency(source: string, name: string) {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`Measurement harness needs Vite ${name}.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, measurement, measurements, tables, db, readonly }) => {
    const [vue, naive, piniaModule, component, authModule] = await Promise.all([import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath)]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div'); host.id = 'wb30-measurement-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: db, measurement, measurements, tables, readOnly: readonly });
    const pinia = piniaModule.createPinia(); const auth = authModule.useAuthStore(pinia); auth.setApiBaseUrl('/');
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, props) }) }); app.use(pinia); app.mount(host);
    (window as FixtureWindow).wb30Harness = {
      context: async (target, empty = false) => { props.targetDb = target; props.measurement = empty ? null : { ...measurement, columns: measurement.columns.map((column) => ({ ...column })) }; await vue.nextTick(); },
      refreshSchema: async () => { props.measurement = { ...measurement, columns: measurement.columns.map((column) => ({ ...column })) }; props.measurements = [...measurements]; await vue.nextTick(); },
      refreshAuth: async () => { auth.state = { ...auth.state }; await vue.nextTick(); },
      readonly: async (value) => { props.readOnly = value; await vue.nextTick(); }, unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, measurement, measurements, tables, db: east, readonly });
  await expect(surface(page).locator('.measurement-grid .n-data-table-td').first()).toBeVisible();
}
async function harness(page: Page, action: keyof Harness, value?: string, flag = false) {
  await page.evaluate(async ({ action, value, flag }) => {
    const target = (window as FixtureWindow).wb30Harness;
    if (action === 'context') await target.context(value ?? '', flag);
    else if (action === 'readonly') await target.readonly(flag);
    else if (action === 'unmount') target.unmount();
    else await target[action]();
  }, { action, value, flag });
}
async function installDelayedFile(page: Page) {
  await page.evaluate(() => {
    const original = File.prototype.text; let release!: () => void;
    const timer = setTimeout(() => release(), 10_000); const wait = new Promise<void>((resolve) => { release = () => { clearTimeout(timer); resolve(); }; });
    (window as FixtureWindow).wb30FileGate = { release, completed: false };
    File.prototype.text = async function () {
      if (this.name !== 'wb30-late.csv') return original.call(this);
      try { await wait; return await original.call(this); }
      finally { File.prototype.text = original; (window as FixtureWindow).wb30FileGate!.completed = true; }
    };
  });
}
async function exportJson(page: Page): Promise<Array<Record<string, unknown>>> {
  const received = page.waitForEvent('download', { timeout: 5_000 });
  await surface(page).getByRole('button', { name: '导出 JSON', exact: true }).click();
  const download = await received;
  try {
    const file = await download.path(); if (!file || !isAbsolute(file)) throw new Error('Fixture download path must be absolute.');
    expect(await download.failure()).toBeNull(); expect((await stat(file)).size).toBeLessThanOrEqual(1_048_576);
    const parsed = JSON.parse(await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(5_000) })) as unknown;
    if (!Array.isArray(parsed) || parsed.length > 500) throw new Error('Measurement fixture export exceeded500 rows.');
    return parsed as Array<Record<string, unknown>>;
  } finally { await download.delete(); }
}
