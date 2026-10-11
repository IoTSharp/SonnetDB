import { expect, test, type Page, type Route } from '@playwright/test';

// Actual routed Vue plus an explicit Vite prop/program-entry harness. All
// authentication and API responses are fixtures, not Server authorization,
// instance persistence, desktop, installation or release evidence.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const topic = 'Telemetry:Original';
const secret = 'OldMqPayloadMustDisappear';
const headerSecret = 'OldMqHeaderMustDisappear';
const consumerSecret = 'OldMqConsumerMustDisappear';
const writeDraft = 'OldMqPublishDraftMustDisappear';
const timestamp = '2026-10-06T00:00:00.000Z';

type ReadAction = 'topics' | 'browse' | 'stats' | 'offsets' | 'retention';
interface ReadRequest { database: string; action: ReadAction; body: Record<string, unknown> }
interface WriteRequest { database: string; action: string; body: Record<string, unknown> }
interface Evidence { reads: ReadRequest[]; writes: WriteRequest[]; unexpected: string[] }
interface FixtureOptions {
  read?: (route: Route, request: ReadRequest) => Promise<void>;
  write?: (route: Route, request: WriteRequest) => Promise<void>;
}
type ProgramAction = 'browseFromInput' | 'refreshAll' | 'stagePublish' | 'stageAckFromForm' | 'stageAckSelected' | 'stageAckHighWater'
  | 'confirmPendingOperations' | 'nextPage' | 'seekByTime';
interface Harness {
  setDatabase: (value: string) => Promise<void>;
  setTopic: (value: string) => Promise<void>;
  refreshTopics: () => Promise<void>;
  setOffset: (value: number) => Promise<void>;
  setReadOnly: (value: boolean) => Promise<void>;
  closeResult: () => Promise<void>;
  invoke: (action: ProgramAction) => Promise<void>;
  unmount: () => void;
}
type FixtureWindow = Window & { wb22MqHarness: Harness; __wb22Exports?: string[] };

test.setTimeout(30_000);

test('routed MQ preserves original database and Topic case/colon identity across same-name databases', async ({ page }) => {
  const evidence = await prepare(page);
  await openMq(page);
  await assertIdentity(page, east, 'normal');
  await messagesTab(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText('East MQ payload');
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await assertIdentity(page, west, 'normal');
  await messagesTab(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText('West MQ payload');
  expect(new URL(page.url()).searchParams.get('node')).toBe(topic);
  expect(evidence.reads.some((item) => item.database === east && item.action === 'browse')).toBe(true);
  expect(evidence.reads.some((item) => item.database === west && item.action === 'browse')).toBe(true);
  assertFixtureEvidence(evidence);
});

test('empty and ordinary error states clear old messages and redact fixture error payloads', async ({ page }) => {
  let state: 'normal' | 'empty' | 'error' = 'normal';
  const evidence = await prepare(page, { read: (route, request) => request.action === 'browse' && state === 'error'
    ? json(route, { message: secret, headers: { leak: headerSecret } }, 500)
    : request.action === 'browse' && state === 'empty' ? json(route, { messages: [] }) : defaultRead(route, request, secret) });
  await openMq(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText(secret);
  state = 'empty';
  await browse(page);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'empty');
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(0);
  await expect(surface(page).locator('.mq-payload-preview')).toHaveCount(0);
  state = 'error';
  await browse(page);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'error');
  await expect(surface(page)).not.toContainText(secret);
  await expect(surface(page)).not.toContainText(headerSecret);
  const panel = await openResult(page);
  await expect(panel).not.toContainText(secret);
  await expect(panel).not.toContainText(headerSecret);
  expect(JSON.stringify(await historyEntries(page))).not.toContain(secret);
  assertFixtureEvidence(evidence);
});

for (const status of [401, 403]) {
  test(`Browse ${status} clears messages/headers/trend/result/draft/approval and latches through empty identities`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.action === 'browse'
      ? json(route, { message: secret, headers: { leak: headerSecret } }, status) : defaultRead(route, request, secret) });
    await openMq(page);
    await mountHarness(page);
    await messagesTab(page);
    await expect(surface(page).locator('.mq-payload-preview')).toContainText(secret);
    await expect(surface(page).locator('.mq-headers')).toContainText(headerSecret);
    const panel = await openResult(page);
    await panel.locator('.n-tabs-tab[data-name="raw"]').click();
    await expect(panel.locator('.workbench-result-panel__result')).toContainText(secret);
    await page.evaluate(() => (window as FixtureWindow).wb22MqHarness.closeResult());
    await expect(panel).not.toBeVisible();
    await mqTab(page, '概览');
    await surface(page).getByRole('button', { name: 'Sample', exact: true }).click();
    await expect(surface(page).locator('.mq-sparkline svg path')).not.toHaveCount(0);
    await stageDraft(page);
    deny = true;
    // The real setup entry dispatches a read while the original approval is
    // still open. This intentionally tests the program-entry boundary.
    await program(page, 'browseFromInput');
    await assertPermissionHidden(page);
    const deniedReadCount = evidence.reads.length;
    await setDatabase(page, '');
    await expect(surface(page)).toHaveAttribute('data-database', '');
    await assertPermissionHidden(page);
    await setDatabase(page, east);
    await assertPermissionHidden(page);
    await setTopic(page, '');
    await assertPermissionHidden(page);
    await setTopic(page, topic);
    await assertPermissionHidden(page);
    await page.evaluate(() => (window as FixtureWindow).wb22MqHarness.refreshTopics());
    await program(page, 'refreshAll');
    await assertPermissionHidden(page);
    expect(evidence.reads).toHaveLength(deniedReadCount);
    await expect(surface(page).getByRole('button', { name: '发布测试消息', exact: true })).toBeDisabled();
    assertFixtureEvidence(evidence);
  });
}

for (const action of ['stats', 'offsets', 'retention'] as const) {
  test(`metadata ${action} 403 cannot preserve old content or be refilled by concurrent metadata successes`, async ({ page }) => {
    let deny = false;
    const evidence = await prepare(page, { read: (route, request) => deny && request.action === action
      ? json(route, { message: secret, consumerGroup: consumerSecret }, 403) : defaultRead(route, request, secret) });
    await openMq(page);
    await mountHarness(page);
    await expect(surface(page).locator('.mq-consumer-name')).toContainText(consumerSecret);
    await messagesTab(page);
    await expect(surface(page).locator('.mq-payload-preview')).toContainText(secret);
    await stageDraft(page);
    deny = true;
    await program(page, 'refreshAll');
    await assertPermissionHidden(page);
    expect(evidence.reads.some((item) => item.action === action)).toBe(true);
    assertFixtureEvidence(evidence);
  });
}

test('Topics 401 after a loaded page clears old resource metadata, payload and staged approval', async ({ page }) => {
  let deny = false;
  const evidence = await prepare(page, { read: (route, request) => deny && request.action === 'topics'
    ? json(route, { message: secret, topics: [{ topic: 'UnauthorizedTopicMustDisappear' }] }, 401) : defaultRead(route, request, secret) });
  await openMq(page);
  await mountHarness(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText(secret);
  await stageDraft(page);
  deny = true;
  await program(page, 'refreshAll');
  await assertPermissionHidden(page);
  await expect(surface(page)).not.toContainText('UnauthorizedTopicMustDisappear');
  assertFixtureEvidence(evidence);
});

test('explicit fixture publish 403 consumes the approval and clears draft and old read content', async ({ page }) => {
  const evidence = await prepare(page, { read: (route, request) => defaultRead(route, request, secret),
    write: (route) => json(route, { message: secret }, 403) });
  await openMq(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText(secret);
  await stageDraft(page);
  // This is an expressly intercepted fixture dispatch, never a real publish.
  await approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await assertPermissionHidden(page);
  expect(evidence.writes[0]).toMatchObject({ database: east, action: 'publish' });
  assertFixtureEvidence(evidence, 1);
});

test('readonly host retains browser/JSONL export while buttons and direct write/approval entries stay gated', async ({ page }) => {
  const evidence = await prepare(page);
  await openMq(page);
  await mountHarness(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(1);
  await stageDraft(page);
  await page.evaluate(() => (window as FixtureWindow).wb22MqHarness.setReadOnly(true));
  await assertIdentity(page, east, 'readonly');
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page).locator('.mq-publisher')).toHaveCount(0);
  await expect(surface(page).getByRole('button', { name: '发布测试消息', exact: true })).toBeDisabled();
  await expect(surface(page).getByTitle('导入消息文件', { exact: true })).toBeDisabled();
  await program(page, 'stagePublish');
  await program(page, 'stageAckFromForm');
  await program(page, 'stageAckSelected');
  await program(page, 'confirmPendingOperations');
  await expect(approval(page)).toHaveCount(0);
  await messagesTab(page);
  await browse(page);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText('East MQ payload');
  expect(await exportJsonl(page)).toHaveLength(1);
  await mqTab(page, '消费者组');
  await expect(surface(page).getByRole('button', { name: 'Stage ack', exact: true })).toBeDisabled();
  await expect(surface(page).getByRole('button', { name: 'Selected', exact: true })).toBeDisabled();
  assertFixtureEvidence(evidence);
});

test('late Browse cannot cross same-Topic database switching or refill an unmounted component', async ({ page }) => {
  let hold = false;
  let pending = 0;
  let completed = 0;
  const stale = 'LateEastMqPayloadMustNotRender';
  const gate = boundedGate();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (hold && request.database === east && request.action === 'browse') {
      pending += 1;
      await gate.wait;
      await finishLate(route, request, stale, () => { completed += 1; });
    } else await defaultRead(route, request);
  } });
  try {
    await openMq(page);
    await mountHarness(page);
    await messagesTab(page);
    await expect(surface(page).locator('.mq-offset-button')).toHaveCount(1);
    hold = true;
    await browse(page);
    await expect.poll(() => pending, { timeout: 5_000 }).toBe(1);
    await setDatabase(page, west);
    await expect(surface(page)).toHaveAttribute('data-database', west);
    await expect(surface(page).locator('.mq-payload-preview')).toContainText('West MQ payload');
    await page.evaluate(() => (window as FixtureWindow).wb22MqHarness.unmount());
    await expect(surface(page)).toHaveCount(0);
    const retainedHistoryCount = (await historyEntries(page)).length;
    gate.release();
    await expect.poll(() => completed, { timeout: 5_000 }).toBe(1);
    await expect(page.locator('body')).not.toContainText(stale);
    expect(await historyEntries(page)).toHaveLength(retainedHistoryCount);
    expect(JSON.stringify(await historyEntries(page))).not.toContain(stale);
    assertFixtureEvidence(evidence);
  } finally { gate.release(); }
});

test('a newer Browse and database ABA both invalidate earlier responses without late history mutation', async ({ page }) => {
  let phase: 'idle' | 'newer' | 'aba' = 'idle';
  const pending = new Set<string>();
  const completed = new Set<string>();
  const gates = { newer: boundedGate(), aba: boundedGate() };
  const current = 'CurrentMqResponseMustRemain';
  const evidence = await prepare(page, { read: async (route, request) => {
    if (phase !== 'idle' && request.action === 'browse' && request.database === east && !pending.has(phase)) {
      const captured = phase;
      pending.add(captured);
      await gates[captured].wait;
      await finishLate(route, request, `StaleMq${captured}MustNotRender`, () => { completed.add(captured); });
    } else await defaultRead(route, request, current);
  } });
  try {
    await openMq(page);
    await mountHarness(page);
    await messagesTab(page);
    await expect(surface(page).locator('.mq-payload-preview')).toContainText(current);
    phase = 'newer';
    await browse(page);
    await expect.poll(() => pending.has('newer'), { timeout: 5_000 }).toBe(true);
    await program(page, 'browseFromInput');
    await expect(surface(page).locator('.mq-payload-preview')).toContainText(current);
    const newerHistoryCount = (await historyEntries(page)).length;
    gates.newer.release();
    await expect.poll(() => completed.has('newer'), { timeout: 5_000 }).toBe(true);
    await expect(surface(page)).not.toContainText('StaleMqnewerMustNotRender');
    expect(await historyEntries(page)).toHaveLength(newerHistoryCount);
    phase = 'aba';
    await browse(page);
    await expect.poll(() => pending.has('aba'), { timeout: 5_000 }).toBe(true);
    await setDatabase(page, west);
    await setDatabase(page, east);
    await expect(surface(page).locator('.mq-payload-preview')).toContainText(current);
    const abaHistoryCount = (await historyEntries(page)).length;
    gates.aba.release();
    await expect.poll(() => completed.has('aba'), { timeout: 5_000 }).toBe(true);
    await expect(surface(page)).not.toContainText('StaleMqabaMustNotRender');
    expect(await historyEntries(page)).toHaveLength(abaHistoryCount);
    expect(JSON.stringify(await historyEntries(page))).not.toContain('StaleMq');
    assertFixtureEvidence(evidence);
  } finally { gates.newer.release(); gates.aba.release(); }
});

test('overreturned fixture window exports only retained rows and advances from their real last offset', async ({ page }) => {
  const sentinel = 'MqOverflowTailMustNotExport';
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action !== 'browse' || request.body.maxCount !== 25) return defaultRead(route, request);
    const offset = Number(request.body.fromOffset ?? 0);
    if (offset === 0) return json(route, { messages: Array.from({ length: 30 }, (_, index) => message(index * 2, index >= 25 ? sentinel : `Preview:${index}`)) });
    return json(route, { messages: [message(offset, 'NextFixtureOffset')] });
  } });
  await openMq(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(1);
  await surface(page).locator('.mq-toolbar__limit').click();
  await page.getByText('25 messages', { exact: true }).click();
  await surface(page).getByPlaceholder('偏移量', { exact: true }).fill('0');
  await browse(page);
  await expect(surface(page).getByTestId('mq-preview-budget')).toContainText(/truncated|截断/u);
  const exported = await exportJsonl(page);
  expect(exported).toHaveLength(25);
  expect(exported.at(-1)?.offset).toBe(48);
  expect(JSON.stringify(exported)).not.toContain(sentinel);
  // Metadata fixture advertises a larger high-water offset for this window.
  await surface(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(surface(page).locator('.mq-payload-preview')).toContainText('NextFixtureOffset');
  const windows = evidence.reads.filter((request) => request.action === 'browse' && request.body.maxCount === 25);
  expect(windows.map((request) => request.body.fromOffset)).toEqual([0, 49]);
  const history = (await historyEntries(page)).filter((item) => item.model === 'mq' && item.rowCount === 25);
  expect(history.some((item) => item.completeness === 'truncated')).toBe(true);
  assertFixtureEvidence(evidence);
});

test('unsafe numeric message/input offsets cannot dispatch Next, Ack or a new Browse', async ({ page }) => {
  const unsafeOffset = Number.MAX_SAFE_INTEGER + 1;
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action === 'browse') return json(route, { messages: [message(unsafeOffset, 'UnsafeOffsetFixture')] });
    if (request.action === 'topics') return json(route, { topics: [{ topic, messageCount: 1, nextOffset: unsafeOffset }] });
    if (request.action === 'stats') return json(route, { topic, messageCount: 1, nextOffset: unsafeOffset, consumerOffsets: { [consumerSecret]: 0 } });
    if (request.action === 'offsets') return json(route, { topic, nextOffset: unsafeOffset, consumers: [{ consumerGroup: consumerSecret, committedOffset: 0, lag: 1 }] });
    return defaultRead(route, request);
  } });
  await openMq(page);
  await mountHarness(page);
  await messagesTab(page);
  await expect(surface(page).getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  const before = evidence.reads.filter((request) => request.action === 'browse').length;
  await program(page, 'nextPage');
  await program(page, 'stageAckSelected');
  // An unsafe high-water must be rejected before subtraction; subtracting
  // one from this fixture would produce the apparently safe MAX_SAFE value.
  await program(page, 'stageAckHighWater');
  await expect(approval(page)).toHaveCount(0);
  await page.evaluate((offset) => (window as FixtureWindow).wb22MqHarness.setOffset(offset), unsafeOffset);
  await program(page, 'browseFromInput');
  expect(evidence.reads.filter((request) => request.action === 'browse')).toHaveLength(before);
  await expect(approval(page)).toHaveCount(0);
  assertFixtureEvidence(evidence);
});

test('longContent bounds four payload formats and headers while JSONL retains the complete loaded message', async ({ page }) => {
  const tail = 'LongMqTailOnlyInFullJsonl';
  const value = JSON.stringify({ text: 'L'.repeat(8192), tail });
  const payload = Buffer.from(value).toString('base64');
  const headers = Object.fromEntries(Array.from({ length: 40 }, (_, index) => [`Header:${index}`, 'H'.repeat(180)]));
  const evidence = await prepare(page, { read: (route, request) => request.action === 'browse'
    ? json(route, { messages: [{ ...message(10, ''), payload, headers }] }) : defaultRead(route, request) });
  await openMq(page);
  await messagesTab(page);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'longContent');
  await expect(surface(page).getByTestId('mq-payload-budget')).toContainText(/4096|4,096/u);
  await expect(surface(page).getByTestId('mq-header-budget')).toContainText(/truncated|截断/u);
  expect((await surface(page).locator('.mq-headers pre').innerText()).length).toBeLessThanOrEqual(4096);
  for (const format of ['text', 'json', 'hex', 'base64']) {
    await surface(page).locator(`.mq-payload-tabs .n-tabs-tab[data-name="${format}"]`).click();
    const preview = await surface(page).locator('.mq-payload-preview').innerText();
    expect(preview.length).toBeLessThanOrEqual(format === 'hex' ? 12_288 : format === 'base64' ? 5_464 : 4_096);
    expect(preview).not.toContain(tail);
  }
  const exported = await exportJsonl(page);
  expect(exported).toHaveLength(1);
  expect(exported[0].payloadBase64).toBe(payload);
  expect(exported[0].headers).toEqual(headers);
  expect(Buffer.from(String(exported[0].payloadBase64), 'base64').toString()).toBe(value);
  await surface(page).getByRole('button', { name: '发布测试消息', exact: true }).click();
  await expect(surface(page).getByPlaceholder('Payload', { exact: true })).not.toHaveValue(value);
  assertFixtureEvidence(evidence);
});

test('unknown explicit fixture publish consumes one approval and is not replayed on refresh or direct confirmation', async ({ page }) => {
  const evidence = await prepare(page, { write: (route) => json(route, { message: 'Fixture response lost after dispatch' }, 503) });
  await openMq(page);
  await mountHarness(page);
  await messagesTab(page);
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(1);
  await stageDraft(page);
  await approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click();
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page)).toContainText(/未知|待核对/u);
  await expect.poll(async () => (await historyEntries(page)).find((item) => item.title === 'SonnetMQ operations')?.status, { timeout: 5_000 }).toBe('unknown');
  expect(evidence.writes).toHaveLength(1);
  await program(page, 'refreshAll');
  await program(page, 'confirmPendingOperations');
  await expect(approval(page)).toHaveCount(0);
  expect(evidence.writes).toHaveLength(1);
  assertFixtureEvidence(evidence, 1);
});

function surface(page: Page) { return page.getByTestId('workbench-mq'); }
function approval(page: Page) { return page.getByRole('dialog', { name: 'SonnetMQ staged operations' }); }
function resultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({ has: page.locator('.workbench-result-panel__title').filter({ hasText: /^SonnetMQ operation result$/u }) });
}
async function openResult(page: Page) {
  await surface(page).getByTestId('mq-open-result').click();
  const panel = resultPanel(page);
  await expect(panel).toBeVisible();
  return panel;
}
async function openMq(page: Page): Promise<void> {
  await page.goto(`/admin/app/sql?tool=mq&database=${encodeURIComponent(east)}&model=mq&node=${encodeURIComponent(topic)}`);
  await expect(surface(page)).toHaveAttribute('data-database', east);
}
async function assertIdentity(page: Page, database: string, state: string): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `mq:${topic}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', state);
}
async function mqTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function messagesTab(page: Page): Promise<void> { await mqTab(page, '消息'); }
async function browse(page: Page): Promise<void> { await surface(page).getByRole('button', { name: '浏览', exact: true }).click(); }
async function stageDraft(page: Page): Promise<void> {
  await surface(page).getByRole('button', { name: '发布测试消息', exact: true }).click();
  await surface(page).getByPlaceholder('Topic', { exact: true }).fill(topic);
  await surface(page).getByPlaceholder('Headers, one key=value per line').fill(`source=${writeDraft}`);
  await surface(page).getByPlaceholder('Payload', { exact: true }).fill(writeDraft);
  await surface(page).getByRole('button', { name: 'Stage publish', exact: true }).click();
  await expect(approval(page)).toContainText('确认执行 1 项操作');
}
async function assertPermissionHidden(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('mq-permission')).toBeVisible();
  await expect(surface(page).locator('.mq-body')).toHaveCount(0);
  await expect(surface(page).locator('.mq-monitor')).toHaveCount(0);
  await expect(surface(page).locator('.mq-headline-stats')).toHaveCount(0);
  await expect(surface(page).locator('.mq-payload-preview')).toHaveCount(0);
  await expect(surface(page).locator('.mq-headers')).toHaveCount(0);
  await expect(surface(page).locator('.mq-publisher')).toHaveCount(0);
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page)).not.toContainText(secret);
  await expect(surface(page)).not.toContainText(headerSecret);
  await expect(surface(page)).not.toContainText(consumerSecret);
  await expect(surface(page)).not.toContainText(writeDraft);
}
async function setDatabase(page: Page, value: string): Promise<void> {
  await page.evaluate((value) => (window as FixtureWindow).wb22MqHarness.setDatabase(value), value);
}
async function setTopic(page: Page, value: string): Promise<void> {
  await page.evaluate((value) => (window as FixtureWindow).wb22MqHarness.setTopic(value), value);
}
async function program(page: Page, action: ProgramAction): Promise<void> {
  await page.evaluate((action) => (window as FixtureWindow).wb22MqHarness.invoke(action), action);
}
function boundedGate() {
  let release!: () => void;
  let released = false;
  const timer = setTimeout(() => release(), 10_000);
  const wait = new Promise<void>((resolve) => { release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } }; });
  return { wait, release };
}
async function finishLate(route: Route, request: ReadRequest, payload: string, complete: () => void): Promise<void> {
  try { await defaultRead(route, request, payload); }
  catch (error) { if (!/closed|cancel|abort|intercept/iu.test(String(error))) throw error; }
  finally { complete(); }
}
async function mountHarness(page: Page): Promise<void> {
  const componentPath = '/src/components/SonnetMqWorkbench.vue';
  const authPath = '/src/stores/auth.ts';
  const [componentSource, authSource] = await Promise.all([
    page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()),
    page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text()),
  ]);
  function dependency(source: string, name: string): string {
    const match = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(source);
    if (!match) throw new Error(`MQ harness needs the Vite ${name} module.`);
    return match[1];
  }
  const modules = { vue: dependency(componentSource, 'vue'), naive: dependency(componentSource, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, topic }) => {
    const [vue, naive, piniaModule, component, auth] = await Promise.all([
      import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath),
    ]);
    const root = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null;
    root?.__vue_app__?.unmount();
    const host = document.createElement('div');
    host.id = 'wb22-mq-harness'; host.style.height = '100vh'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, topic, topics: [{ topic, messageCount: 96, nextOffset: 96 }], readOnly: false });
    const componentRef = vue.ref(null);
    const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, { ...props, ref: componentRef }) }) });
    app.use(pinia);
    auth.useAuthStore(pinia).setApiBaseUrl('/');
    app.mount(host);
    const setup = (): Record<string, unknown> => componentRef.value.$.setupState;
    const allowed: ProgramAction[] = ['browseFromInput', 'refreshAll', 'stagePublish', 'stageAckFromForm', 'stageAckSelected', 'stageAckHighWater', 'confirmPendingOperations', 'nextPage', 'seekByTime'];
    (window as FixtureWindow).wb22MqHarness = {
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); },
      setTopic: async (value) => { props.topic = value; props.topics = value ? [{ topic: value, messageCount: 96, nextOffset: 96 }] : []; await vue.nextTick(); },
      refreshTopics: async () => { props.topics = [{ topic, messageCount: 96, nextOffset: 96 }]; await vue.nextTick(); },
      setOffset: async (value) => { setup().fromOffset = value; await vue.nextTick(); },
      setReadOnly: async (value) => { props.readOnly = value; await vue.nextTick(); },
      closeResult: async () => { setup().resultVisible = false; await vue.nextTick(); },
      invoke: async (action) => {
        if (!allowed.includes(action)) throw new Error('MQ program action is not in the fixture allowlist.');
        const method = setup()[action];
        if (typeof method !== 'function') throw new Error(`MQ setup entry ${action} is unavailable.`);
        await method(); await vue.nextTick();
      },
      unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, topic });
}
async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] };
  const fixtureOrigin = new URL(test.info().project.use.baseURL as string).origin;
  await page.addInitScript(({ database }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const fixture = window as FixtureWindow;
    fixture.__wb22Exports = [];
    const createUrl = URL.createObjectURL.bind(URL);
    URL.createObjectURL = ((value: Blob) => { void value.text().then((content) => fixture.__wb22Exports?.push(content)); return createUrl(value); }) as typeof URL.createObjectURL;
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
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb22-fixture', organization: 'Workbench fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    if (path === '/v1/semantic-search/status') return json(route, { enabled: false, ready: false, reason: 'fixture' });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    if (matched && [east, west].includes(matched[1])) {
      const database = matched[1]; const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/s3' || suffix === '/graphs') return json(route, []);
      if (suffix === '/mq/topics') {
        const request: ReadRequest = { database, action: 'topics', body: {} };
        evidence.reads.push(request);
        return options.read ? options.read(route, request) : defaultRead(route, request);
      }
      const prefix = `/mq/${topic}/`;
      if (suffix.startsWith(prefix)) {
        const action = suffix.slice(prefix.length);
        const body = route.request().postDataJSON() as Record<string, unknown> | null;
        if (['browse', 'stats', 'offsets', 'retention'].includes(action)) {
          const request: ReadRequest = { database, action: action as ReadAction, body: body ?? {} };
          evidence.reads.push(request);
          return options.read ? options.read(route, request) : defaultRead(route, request);
        }
        const request: WriteRequest = { database, action, body: body ?? {} };
        evidence.writes.push(request);
        return options.write ? options.write(route, request) : json(route, { code: 'wb22_fixture_write_forbidden' }, 501);
      }
    }
    evidence.unexpected.push(`${route.request().method()} ${path}${url.search}`);
    return json(route, { code: 'wb22_contract_not_mocked', message: path }, 501);
  });
  return evidence;
}
function message(offset: number, payload: string) {
  return { topic, offset, timestampUtc: timestamp, headers: { source: headerSecret }, payload: Buffer.from(payload).toString('base64') };
}
async function defaultRead(route: Route, request: ReadRequest, payload = `${request.database === east ? 'East' : 'West'} MQ payload`): Promise<void> {
  if (request.action === 'topics') return json(route, { topics: [{ topic, messageCount: 96, nextOffset: 96 }] });
  if (request.action === 'stats') return json(route, { topic, messageCount: 96, nextOffset: 96, consumerOffsets: { [consumerSecret]: 8 } });
  if (request.action === 'offsets') return json(route, { topic, nextOffset: 96, consumers: [{ consumerGroup: consumerSecret, committedOffset: 8, lag: 88 }] });
  if (request.action === 'retention') return json(route, { topic, retainedStartOffset: 0, retainedEndOffset: 95, retainedMessages: 96, trimmedBeforeOffset: 0,
    retentionMaxAgeSeconds: 86400, retentionMaxBytes: 1_048_576, retentionIntervalSeconds: 60, trimAcknowledgedMessages: true,
    ackRetentionMinOffsetDelta: 1, segmentMaxBytes: 8192, hotTailMaxBytes: 4096, segmentCacheSize: 2 });
  return json(route, { messages: [message(Number(request.body.fromOffset ?? 10), payload)] });
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
async function exportJsonl(page: Page): Promise<Array<Record<string, unknown>>> {
  const before = await page.evaluate(() => (window as FixtureWindow).__wb22Exports?.length ?? 0);
  await surface(page).getByTestId('mq-export-jsonl').click();
  await expect.poll(() => page.evaluate(() => (window as FixtureWindow).__wb22Exports?.length ?? 0), { timeout: 5_000 }).toBeGreaterThan(before);
  const content = await page.evaluate((offset) => (window as FixtureWindow).__wb22Exports?.[offset] ?? '', before);
  if (content.length > 1_048_576) throw new Error('MQ export exceeded the fixture read budget.');
  const lines = content.trim().split(/\r?\n/u);
  if (lines.length > 1000) throw new Error('MQ export exceeded the retained preview row budget.');
  return lines.map((line) => JSON.parse(line) as Record<string, unknown>);
}
