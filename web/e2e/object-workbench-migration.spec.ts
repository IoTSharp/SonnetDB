import { expect, test, type Page, type Route } from '@playwright/test';

// Real routed Vue and Vite component entries; authentication and all APIs are
// fixtures. This does not prove Server permissions, semantic budgets, complete
// write outcomes, three hosts, installation, AOT or release acceptance.
const east = 'FactoryDB:East';
const west = 'FactoryDB:West';
const bucket = 'InspectionMedia:Original';
const firstKey = 'Images:North/DeviceID:Original.png';
const secondKey = 'Images:North/Other:Device.png';
const version = 'Version:Original';
const oldPayload = 'OldObjectPayloadMustDisappear';
const oldPolicy = 'OldObjectPolicyMustDisappear';
const oldTag = 'OldObjectTagMustDisappear';
const oldHold = 'OldObjectHoldMustDisappear';
const oldAudit = 'OldObjectAuditMustDisappear';
const oldDraft = 'OldObjectUploadDraftMustDisappear';
const oldSemantic = 'OldObjectSemanticMustDisappear';
const now = '2026-10-06T00:00:00.000Z';
const bucketInfo = { name: bucket, purpose: 'fixture', createdUtc: now, updatedUtc: now, objectCount: 2, totalBytes: 8192 };
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==', 'base64');

type ReadAction = 'list' | 'buckets' | 'stats' | 'lifecycle' | 'retention' | 'quota' | 'policy' | 'semantic-options'
  | 'runtime' | 'tags' | 'hold' | 'versions' | 'processing' | 'thumbnail' | 'audit' | 'multipart' | 'range' | 'download' | 'semantic' | 'protected';
interface ReadRequest { action: ReadAction; database: string; key: string; query: Record<string, string>; headers: Record<string, string>; method: string }
interface Evidence { reads: ReadRequest[]; writes: string[]; unexpected: string[] }
interface FixtureOptions { read?: (route: Route, request: ReadRequest) => Promise<void> }
type Action = 'refreshAll' | 'loadObjects' | 'loadGovernance' | 'loadVersions' | 'loadAudit' | 'loadMultipartSessions'
  | 'loadSelectedProcessing' | 'loadPreview' | 'loadVersionPreview' | 'downloadSelectedObject' | 'runSemanticSearch'
  | 'stageCreateBucket' | 'stageDeleteBucket' | 'stageUploadFile' | 'stageUploadText' | 'stageSetTags' | 'stageCopySelected'
  | 'stageDeleteCurrent' | 'stageDeleteSelected' | 'stagePresign' | 'stageSetLifecycle' | 'stageApplyLifecycle'
  | 'stageSetRetention' | 'stageSetQuota' | 'stageSetPolicy' | 'stageSetLegalHold' | 'stageSetSemanticOptions'
  | 'stageSemanticBackfill' | 'stageRequeueSelectedObject' | 'stageInitiateMultipart' | 'stageUploadPart'
  | 'stageCompleteMultipart' | 'stageAbortMultipart' | 'confirmPendingOperations';
interface Snapshot {
  keys: string[]; selectedKey: string; preview: string; policy: string; tags: string; hold: string; versions: string[];
  processing: string | null; thumbnail: string; semantic: string; hitUrls: string[]; audit: string; multipart: string;
  drafts: string; pending: number; resultRows: number; truncated: boolean; hasMore: boolean;
}
interface Harness {
  invoke: (action: Action, argument?: string | boolean) => Promise<void>;
  setDatabase: (value: string) => Promise<void>; setBucket: (value: string) => Promise<void>;
  setReadonly: (value: boolean) => Promise<void>; setPrefix: (value: string) => Promise<void>;
  setKey: (value: string) => Promise<void>; setListLimit: (value: number) => Promise<void>;
  setRange: (start: number, length: number, mode?: string) => Promise<void>;
  setMode: (value: string) => Promise<void>; seedValidDrafts: () => Promise<void>;
  setToken: (value: string) => Promise<void>; replaceApi: () => Promise<void>;
  snapshot: () => Snapshot; unmount: () => void;
}
type FixtureWindow = Window & { wb24ObjectHarness: Harness; __wb24Urls: string[]; __wb24UrlOrigins: Array<{ url: string; type: string; size: number; stack: string }>; __wb24Revoked: string[]; __wb24Downloads: string[]; __wb24ArrayBufferSizes: number[] };
test.setTimeout(30_000);

test('Object route keeps original case/colon database, same-name Bucket, key and version identity', async ({ page }) => {
  const evidence = await prepare(page);
  await openObject(page);
  await identity(page, east, 'normal');
  await expect(surface(page).locator('.object-key-button').filter({ hasText: firstKey })).toBeVisible();
  await tab(page, '治理');
  await expect(surface(page).locator('textarea')).toHaveValue(new RegExp(oldPolicy, 'u'));
  await page.locator('.schema-item--database').filter({ hasText: west }).click();
  await identity(page, west, 'normal');
  await tab(page, '对象浏览');
  await expect(surface(page).locator('.object-key-button').filter({ hasText: firstKey })).toBeVisible();
  expect(new URL(page.url()).searchParams.get('node')).toBe(bucket);
  expect(evidence.reads.some((item) => item.database === east && item.action === 'list')).toBe(true);
  expect(evidence.reads.some((item) => item.database === west && item.action === 'list')).toBe(true);
  cleanEvidence(evidence);
});

test('empty and ordinary list error remove prior payload and keep response error bodies out of UI/history', async ({ page }) => {
  let state: 'normal' | 'empty' | 'error' = 'normal';
  const evidence = await prepare(page, { read: (route, request) => request.action === 'list' && state !== 'normal'
    ? state === 'empty' ? json(route, list(request, [])) : json(route, { message: oldPayload }, 500) : defaultRead(route, request) });
  await openObject(page); await mountHarness(page); await ready(page);
  await program(page, 'loadPreview');
  await expect(surface(page).locator('.object-preview')).toContainText(oldPayload);
  state = 'empty'; await program(page, 'loadObjects', true);
  await identity(page, east, 'empty');
  expect((await snapshot(page)).preview).toBe('');
  state = 'error'; await program(page, 'loadObjects', true);
  await identity(page, east, 'error');
  expect((await snapshot(page)).keys).toEqual([]);
  await expect(surface(page)).not.toContainText(oldPayload);
  expect(JSON.stringify(await history(page))).not.toContain(oldPayload);
  cleanEvidence(evidence);
});

// Each test runs exactly two independent page loads. The refusal is armed only
// after actual payloads, derived URLs, valid drafts and an approval are visible.
const deniedPairs: Array<Array<[ReadAction, number]>> = [
  [['list', 401], ['stats', 403]], [['tags', 401], ['hold', 403]],
  [['versions', 401], ['processing', 403]], [['thumbnail', 401], ['audit', 403]],
  [['multipart', 401], ['semantic', 403]], [['runtime', 401], ['protected', 403]],
];
for (const pair of deniedPairs) {
  test(`${pair.map(([action, status]) => `${action} ${status}`).join(' / ')} clears loaded Object payloads, URLs, drafts and approval and latches`, async ({ page }) => {
    let deny: ReadAction | undefined;
    let denyStatus = 403;
    const evidence = await prepare(page, { read: (route, request) => request.action === deny
      ? json(route, { message: oldPayload, policyJson: oldPolicy }, denyStatus) : defaultRead(route, request) });
    for (const [action, status] of pair) {
      deny = undefined;
      await openObject(page); await mountHarness(page); await prime(page);
      const beforeDeny = await snapshot(page);
      const objectUrls = [beforeDeny.thumbnail, ...beforeDeny.hitUrls];
      expect(objectUrls).toHaveLength(2);
      expect(objectUrls.every((url) => url.startsWith('blob:'))).toBe(true);
      deny = action; denyStatus = status;
      if (action === 'list') await program(page, 'loadObjects', true);
      else if (action === 'stats' || action === 'runtime') await program(page, 'loadGovernance', bucket);
      else if (action === 'audit') await program(page, 'loadAudit');
      else if (action === 'multipart') await program(page, 'loadMultipartSessions', true);
      else if (action === 'semantic' || action === 'protected') await program(page, 'runSemanticSearch');
      else if (action === 'versions') await program(page, 'loadVersions');
      else if (action === 'processing' || action === 'thumbnail') await program(page, 'loadSelectedProcessing');
      else {
        await harness(page, 'setKey', secondKey);
        await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
      }
      await permissionCleared(page);
      const count = evidence.reads.length;
      await program(page, 'refreshAll'); await program(page, 'loadObjects', true); await program(page, 'loadPreview');
      await harness(page, 'setDatabase', ''); await harness(page, 'setDatabase', east);
      await harness(page, 'setBucket', ''); await harness(page, 'setBucket', bucket);
      await program(page, 'refreshAll');
      await permissionCleared(page);
      expect(evidence.reads).toHaveLength(count);
      const urls = await page.evaluate(() => ({ made: (window as FixtureWindow).__wb24Urls, origins: (window as FixtureWindow).__wb24UrlOrigins, revoked: (window as FixtureWindow).__wb24Revoked }));
      for (const url of objectUrls) { expect(urls.made).toContain(url); expect(urls.revoked).toContain(url); }
      for (const url of urls.made) {
        const origin = urls.origins.find((item) => item.url === url);
        // The diagnostic trace identified one routed MapLibre worker bootstrap
        // (text/javascript, direct define2 caller at maplibre-gl.js:24:46).
        // Its lifecycle is outside Object; no generic script/type exclusion.
        const routeBootstrap = origin?.type === 'text/javascript'
          && /^\s+at define2 \(https?:\/\/[^/\s]+\/node_modules\/\.vite\/deps\/maplibre-gl\.js(?:\?[^)\s]+)?:24:46\)$/u.test(origin.stack.split('\n')[2] ?? '');
        if (!routeBootstrap) expect(urls.revoked, JSON.stringify(origin)).toContain(url);
      }
    }
    cleanEvidence(evidence);
  });
}

test('readonly Object keeps browser, Range and download but all valid write stages and confirmation remain gated', async ({ page }) => {
  const evidence = await prepare(page);
  await openObject(page); await mountHarness(page); await ready(page);
  await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.seedValidDrafts());
  await program(page, 'stageUploadText'); await expect(approval(page)).toContainText('Put text object');
  await harness(page, 'setReadonly', true); await identity(page, east, 'readonly'); await ready(page);
  await expect(approval(page)).toHaveCount(0);
  await program(page, 'loadPreview'); await expect(surface(page).locator('.object-preview')).toContainText(oldPayload);
  await program(page, 'downloadSelectedObject');
  expect(await page.evaluate(() => (window as FixtureWindow).__wb24Downloads)).toHaveLength(1);
  await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.seedValidDrafts());
  const stages: Action[] = ['stageCreateBucket', 'stageDeleteBucket', 'stageUploadFile', 'stageUploadText', 'stageSetTags', 'stageCopySelected',
    'stageDeleteCurrent', 'stageDeleteSelected', 'stagePresign', 'stageSetLifecycle', 'stageApplyLifecycle', 'stageSetRetention', 'stageSetQuota',
    'stageSetPolicy', 'stageSetLegalHold', 'stageSetSemanticOptions', 'stageSemanticBackfill', 'stageRequeueSelectedObject',
    'stageInitiateMultipart', 'stageUploadPart', 'stageCompleteMultipart', 'stageAbortMultipart'];
  for (const action of stages) { await program(page, action); await program(page, 'confirmPendingOperations'); }
  for (const [section, names] of [
    ['对象浏览', ['Stage create', 'Stage drop', 'Stage delete', 'Stage URL']],
    ['治理', ['Stage save', 'Stage apply', 'Stage retention', 'Stage quota', 'Stage policy', 'Stage hold']],
    ['上传 / 下载', ['Stage file upload', 'Stage text upload', 'Stage tags', 'Stage copy', 'Stage delete']],
    ['图片语义', ['暂存配置', '补录当前对象', '重新入队']],
    ['Multipart', ['Stage initiate', 'Stage upload part', 'Stage complete', 'Stage abort']],
  ] as Array<[string, string[]]>) {
    await tab(page, section);
    for (const name of names) await expect(surface(page).getByRole('button', { name, exact: true })).toBeDisabled();
  }
  expect((await snapshot(page)).pending).toBe(0);
  cleanEvidence(evidence);
});

test('list clamps request 1..1000, truncates before mapping and caps accumulated preview without reusing skipped cursor', async ({ page }) => {
  let mode: 'normal' | 'over' | 'pages' = 'normal';
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action !== 'list' || mode === 'normal') return defaultRead(route, request);
    const count = mode === 'over' ? 1100 : 600;
    const offset = request.query['continuation-token'] ? 600 : 0;
    return json(route, list(request, Array.from({ length: count }, (_, i) => object(`${request.query.prefix ?? ''}Item:${offset + i}`, request.database)), true, 'opaque-next'));
  } });
  await openObject(page); await mountHarness(page); await ready(page);
  mode = 'over'; await harness(page, 'setListLimit', 5000); await program(page, 'loadObjects', true);
  expect(evidence.reads.filter((item) => item.action === 'list').at(-1)?.query['max-keys']).toBe('1000');
  expect((await snapshot(page)).keys).toHaveLength(1000);
  expect((await snapshot(page)).resultRows).toBe(1000);
  expect((await snapshot(page)).hasMore).toBe(false);
  await identity(page, east, 'longContent');
  await expect(surface(page).getByTestId('object-preview-budget')).toBeVisible();
  const before = evidence.reads.length; await surface(page).getByRole('button', { name: 'Load more', exact: true }).isDisabled().then((value) => expect(value).toBe(true));
  await program(page, 'loadObjects', false);
  expect(evidence.reads).toHaveLength(before);
  mode = 'pages'; await harness(page, 'setListLimit', 600); await program(page, 'loadObjects', true); await program(page, 'loadObjects', false);
  expect((await snapshot(page)).keys).toHaveLength(1000); expect((await snapshot(page)).hasMore).toBe(false);
  mode = 'normal'; await harness(page, 'setListLimit', 0); await program(page, 'loadObjects', true);
  expect(evidence.reads.filter((item) => item.action === 'list').at(-1)?.query['max-keys']).toBe('1');
  const listHistory = (await history(page)).filter((entry) => entry.action === 'browse');
  expect(listHistory.some((entry) => entry.completeness === 'truncated')).toBe(true);
  cleanEvidence(evidence);
});

test('wrong list bucket/prefix/entry and nonadvancing continuation are rejected without carrying old rows or repeating token', async ({ page }) => {
  let flaw = '';
  const evidence = await prepare(page, { read: (route, request) => {
    if (request.action !== 'list' || !flaw) return defaultRead(route, request);
    const body = list(request, [object(firstKey, request.database)], flaw === 'token', 'same-token');
    if (flaw === 'bucket') body.bucket = 'Wrong:Bucket';
    if (flaw === 'prefix') body.prefix = 'Wrong:Prefix/';
    if (flaw === 'entry') body.objects[0].bucket = 'Wrong:Bucket';
    if (flaw === 'token' && request.query['continuation-token']) body.nextContinuationToken = request.query['continuation-token'];
    return json(route, body);
  } });
  await openObject(page); await mountHarness(page); await ready(page);
  for (const value of ['bucket', 'prefix', 'entry']) {
    flaw = value; await program(page, 'loadObjects', true);
    await identity(page, east, 'error'); expect((await snapshot(page)).keys).toEqual([]);
  }
  flaw = 'token'; await program(page, 'loadObjects', true); await program(page, 'loadObjects', false);
  expect((await snapshot(page)).hasMore).toBe(false);
  const before = evidence.reads.length; await program(page, 'loadObjects', false); expect(evidence.reads).toHaveLength(before);
  cleanEvidence(evidence);
});

test('Range slices Blob before arrayBuffer, clamps 4096, freezes mode/version and rejects unsafe integer arithmetic before dispatch', async ({ page }) => {
  let delay = false;
  const gate = deferred();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (request.action !== 'range') return defaultRead(route, request);
    if (delay) {
      await gate.wait;
      return range(route, request, 'AAAA');
    }
    // A valid declared window with an oversized fixture Blob proves only the
    // client's slice-before-read boundary, not a conforming Server response.
    return range(route, request, 'A'.repeat(4096) + oldPayload);
  } });
  try {
    await openObject(page); await mountHarness(page); await ready(page);
    await page.evaluate(() => { (window as FixtureWindow).__wb24ArrayBufferSizes = []; });
    await setRange(page, 10, 9000, 'text'); await program(page, 'loadPreview', version);
    const request = evidence.reads.filter((item) => item.action === 'range').at(-1)!;
    expect(request.headers.range).toBe('bytes=10-4105'); expect(request.query.versionId).toBe(version);
    expect((await snapshot(page)).preview).toBe('A'.repeat(4096));
    expect(await page.evaluate(() => (window as FixtureWindow).__wb24ArrayBufferSizes)).toEqual([4096]);
    await expect(surface(page).getByTestId('object-range-budget')).toBeVisible();
    delay = true; await setRange(page, 0, 4, 'hex'); const pending = program(page, 'loadPreview', version);
    await expect.poll(() => evidence.reads.filter((item) => item.action === 'range').length, { timeout: 5_000 }).toBe(2);
    await harness(page, 'setMode', 'base64'); gate.release(); await pending;
    // A mode change invalidates the prior request; it must not reinterpret old bytes.
    expect(['', '41 41 41 41']).toContain((await snapshot(page)).preview);
    delay = false;
    const before = evidence.reads.length;
    for (const [start, length] of [[Number.MAX_SAFE_INTEGER, 2], [0, Number.MAX_SAFE_INTEGER + 1], [1.5, 2]]) {
      await setRange(page, start, length, 'text'); await program(page, 'loadPreview');
    }
    expect(evidence.reads).toHaveLength(before);
    cleanEvidence(evidence);
  } finally { gate.release(); }
});

test('new prefix and database ABA prevent late list payload, continuation, result and history adoption', async ({ page }) => {
  let armed = false; let held = false; const gate = deferred();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (armed && !held && request.action === 'list') {
      held = true; await gate.wait;
      return json(route, list(request, [object(`${request.query.prefix ?? ''}LateOldList`, request.database)], true, 'late-old-token'));
    }
    return defaultRead(route, request);
  } });
  try {
    await openObject(page); await mountHarness(page); await ready(page);
    armed = true; const pending = program(page, 'loadObjects', true);
    await expect.poll(() => held, { timeout: 5_000 }).toBe(true);
    await harness(page, 'setPrefix', 'New:Prefix/');
    await expect.poll(async () => (await snapshot(page)).keys[0], { timeout: 5_000 }).toBe(`New:Prefix/${firstKey}`);
    await harness(page, 'setDatabase', west); await identity(page, west, 'normal');
    await harness(page, 'setDatabase', east); await ready(page);
    gate.release(); await pending;
    expect(JSON.stringify(await snapshot(page))).not.toContain('LateOldList');
    expect(JSON.stringify(await history(page))).not.toContain('LateOldList');
    expect((await snapshot(page)).hasMore).toBe(false);
    cleanEvidence(evidence);
  } finally { gate.release(); }
});

test('selected key/version ABA, late thumbnail and Range, and unmount do not create old URLs or expose old bytes', async ({ page }) => {
  let armed = false; const gates = [deferred(), deferred()]; const seen = [false, false];
  const evidence = await prepare(page, { read: async (route, request) => {
    const index = request.action === 'range' ? 0 : request.action === 'thumbnail' ? 1 : -1;
    if (armed && index >= 0 && !seen[index]) {
      seen[index] = true; await gates[index].wait;
      if (index === 0) return range(route, request, 'LateOldRange');
      return imageBlob(route);
    }
    return defaultRead(route, request);
  } });
  try {
    await openObject(page); await mountHarness(page); await ready(page);
    armed = true; const oldRange = program(page, 'loadPreview', version); const oldThumbnail = program(page, 'loadSelectedProcessing');
    await expect.poll(() => seen.every(Boolean), { timeout: 5_000 }).toBe(true);
    await harness(page, 'setKey', secondKey); await harness(page, 'setKey', firstKey);
    await program(page, 'loadVersionPreview', 'Version:Historical');
    await expect(surface(page).locator('.object-preview')).toContainText('Version:Historical');
    const created = await page.evaluate(() => (window as FixtureWindow).__wb24Urls.length);
    await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.unmount());
    gates.forEach((gate) => gate.release()); await Promise.all([oldRange, oldThumbnail]);
    await expect(surface(page)).toHaveCount(0);
    expect(await page.evaluate(() => (window as FixtureWindow).__wb24Urls.length)).toBe(created);
    expect(JSON.stringify(await history(page))).not.toContain('LateOldRange');
    cleanEvidence(evidence);
  } finally { gates.forEach((gate) => gate.release()); }
});

test('actual auth token and API replacement invalidate old reads and approval while new Axios uses current Authorization', async ({ page }) => {
  let armed = false; let held = false; const gate = deferred();
  const evidence = await prepare(page, { read: async (route, request) => {
    if (armed && !held && request.action === 'range') {
      held = true; await gate.wait; return range(route, request, 'OldCredentialRange');
    }
    return defaultRead(route, request);
  } });
  try {
    await openObject(page); await mountHarness(page); await ready(page);
    await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.seedValidDrafts());
    await program(page, 'stageUploadText'); await expect(approval(page)).toBeVisible();
    armed = true; const pending = program(page, 'loadPreview'); await expect.poll(() => held, { timeout: 5_000 }).toBe(true);
    await harness(page, 'setToken', 'fixture-token-new');
    await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.replaceApi());
    await ready(page); await expect(approval(page)).toHaveCount(0);
    gate.release(); await pending;
    expect((await snapshot(page)).preview).not.toContain('OldCredentialRange');
    await program(page, 'loadPreview');
    expect(evidence.reads.filter((item) => item.action === 'range').at(-1)?.headers.authorization).toBe('Bearer fixture-token-new');
    cleanEvidence(evidence);
  } finally { gate.release(); }
});

function surface(page: Page) { return page.getByTestId('workbench-bucket'); }
function approval(page: Page) { return page.locator('.write-approval'); }
async function openObject(page: Page) { await page.goto(`/admin/app/sql?tool=bucket&database=${encodeURIComponent(east)}&model=bucket&node=${encodeURIComponent(bucket)}`); await identity(page, east, 'normal'); }
async function identity(page: Page, database: string, state: string) {
  await expect(surface(page)).toHaveAttribute('data-database', database); await expect(surface(page)).toHaveAttribute('data-resource-key', `bucket:${bucket}`); await expect(surface(page)).toHaveAttribute('data-page-state', state);
}
async function tab(page: Page, name: string) { await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click(); }
async function program(page: Page, action: Action, argument?: string | boolean) { await page.evaluate(({ action, argument }) => (window as FixtureWindow).wb24ObjectHarness.invoke(action, argument), { action, argument }); }
async function harness(page: Page, method: 'setDatabase' | 'setBucket' | 'setReadonly' | 'setPrefix' | 'setKey' | 'setListLimit' | 'setMode' | 'setToken', value: string | number | boolean) {
  await page.evaluate(({ method, value }) => ((window as FixtureWindow).wb24ObjectHarness[method] as (value: string | number | boolean) => Promise<void>)(value), { method, value });
}
async function setRange(page: Page, start: number, length: number, mode: string) { await page.evaluate(({ start, length, mode }) => (window as FixtureWindow).wb24ObjectHarness.setRange(start, length, mode), { start, length, mode }); }
async function snapshot(page: Page) { return page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.snapshot()); }
async function ready(page: Page) {
  await expect.poll(async () => (await snapshot(page)).keys.length, { timeout: 5_000 }).toBe(2);
  await expect.poll(async () => (await snapshot(page)).tags, { timeout: 5_000 }).toContain(oldTag);
  await expect.poll(async () => (await snapshot(page)).thumbnail, { timeout: 5_000 }).not.toBe('');
}
async function prime(page: Page) {
  await ready(page); await program(page, 'loadPreview'); await expect(surface(page).locator('.object-preview')).toContainText(oldPayload);
  await page.evaluate(() => (window as FixtureWindow).wb24ObjectHarness.seedValidDrafts());
  await program(page, 'runSemanticSearch');
  await tab(page, '图片语义'); await expect(surface(page).locator('.object-semantic-hit')).toContainText(oldSemantic);
  await expect(surface(page).locator('.object-semantic-hit img')).toHaveCount(1);
  await tab(page, '治理'); await expect(surface(page).locator('textarea')).toHaveValue(new RegExp(oldPolicy, 'u'));
  await tab(page, '审计'); await expect(surface(page).locator('.object-audit-grid')).toContainText(oldAudit);
  await tab(page, '上传 / 下载'); await program(page, 'stageUploadText'); await expect(approval(page)).toContainText('Put text object');
  const loaded = await snapshot(page); expect(loaded.tags).toContain(oldTag); expect(loaded.hold).toContain(oldHold); expect(loaded.multipart).not.toBe(''); expect(loaded.thumbnail).not.toBe(''); expect(loaded.hitUrls).toHaveLength(1);
}
async function permissionCleared(page: Page) {
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission'); await expect(surface(page).getByTestId('object-permission')).toBeVisible();
  await expect(surface(page).locator('.object-body')).toHaveCount(0); await expect(approval(page)).toHaveCount(0);
  const state = await snapshot(page);
  expect(state.keys).toEqual([]); expect(state.preview).toBe(''); expect(state.policy).toBe(''); expect(state.tags).toBe(''); expect(state.hold).toBe('');
  expect(state.versions).toEqual([]); expect(state.processing).toBeNull(); expect(state.thumbnail).toBe(''); expect(state.hitUrls).toEqual([]);
  expect(state.semantic).toBe(''); expect(state.audit).toBe('[]'); expect(state.multipart).toBe(''); expect(state.drafts).not.toContain(oldDraft); expect(state.pending).toBe(0); expect(state.resultRows).toBe(0);
  for (const text of [oldPayload, oldPolicy, oldTag, oldHold, oldAudit, oldDraft, oldSemantic]) await expect(surface(page)).not.toContainText(text);
}
function deferred() {
  let resolve!: () => void; let released = false; const timer = setTimeout(() => release(), 10_000);
  const release = () => { if (!released) { released = true; clearTimeout(timer); resolve(); } };
  return { wait: new Promise<void>((done) => { resolve = done; }), release };
}

async function mountHarness(page: Page) {
  const componentPath = '/src/components/ObjectBucketWorkbench.vue'; const authPath = '/src/stores/auth.ts';
  const [source, authSource] = await Promise.all([page.request.get(componentPath, { timeout: 5_000 }).then((response) => response.text()), page.request.get(authPath, { timeout: 5_000 }).then((response) => response.text())]);
  function dependency(content: string, name: string) { const found = new RegExp(`from ["']([^"']*/node_modules/\\.vite/deps/${name}\\.js[^"']*)["']`, 'u').exec(content); if (!found) throw new Error(`Object harness requires ${name}.`); return found[1]; }
  const modules = { vue: dependency(source, 'vue'), naive: dependency(source, 'naive-ui'), pinia: dependency(authSource, 'pinia') };
  await page.evaluate(async ({ modules, componentPath, authPath, database, bucket, bucketInfo }) => {
    const clientPath = '/src/api/client.ts';
    const [vue, naive, piniaModule, component, authModule, clientModule] = await Promise.all([import(modules.vue), import(modules.naive), import(modules.pinia), import(componentPath), import(authPath), import(clientPath)]);
    const oldRoot = document.getElementById('app') as (HTMLElement & { __vue_app__?: { unmount: () => void } }) | null; oldRoot?.__vue_app__?.unmount();
    const host = document.createElement('div'); host.style.height = '100vh'; host.id = 'wb24-object-harness'; document.body.append(host);
    const props = vue.reactive({ targetDb: database, bucket, buckets: [bucketInfo], readOnly: false, permissionDenied: false });
    const componentRef = vue.ref(null); const pinia = piniaModule.createPinia();
    const app = vue.createApp({ render: () => vue.h(naive.NMessageProvider, null, { default: () => vue.h(component.default, { ...props, ref: componentRef }) }) });
    app.use(pinia); const auth = authModule.useAuthStore(pinia); auth.setApiBaseUrl('/'); app.mount(host);
    const setup = (): Record<string, any> => componentRef.value.$.setupState;
    const allowed = ['refreshAll', 'loadObjects', 'loadGovernance', 'loadVersions', 'loadAudit', 'loadMultipartSessions', 'loadSelectedProcessing', 'loadPreview', 'loadVersionPreview', 'downloadSelectedObject', 'runSemanticSearch',
      'stageCreateBucket', 'stageDeleteBucket', 'stageUploadFile', 'stageUploadText', 'stageSetTags', 'stageCopySelected', 'stageDeleteCurrent', 'stageDeleteSelected', 'stagePresign', 'stageSetLifecycle', 'stageApplyLifecycle', 'stageSetRetention', 'stageSetQuota', 'stageSetPolicy', 'stageSetLegalHold', 'stageSetSemanticOptions', 'stageSemanticBackfill', 'stageRequeueSelectedObject', 'stageInitiateMultipart', 'stageUploadPart', 'stageCompleteMultipart', 'stageAbortMultipart', 'confirmPendingOperations'];
    (window as FixtureWindow).wb24ObjectHarness = {
      invoke: async (action, argument) => { if (!allowed.includes(action) || typeof setup()[action] !== 'function') throw new Error(`Object action ${action} is unavailable.`); await setup()[action](argument); await vue.nextTick(); },
      setDatabase: async (value) => { props.targetDb = value; await vue.nextTick(); }, setBucket: async (value) => { props.bucket = value; await vue.nextTick(); },
      setReadonly: async (value) => { props.readOnly = value; await vue.nextTick(); },
      setPrefix: async (value) => { setup().prefixInput = value; setup().applyPrefix(); await vue.nextTick(); },
      setKey: async (value) => { setup().selectObject(value); await vue.nextTick(); }, setListLimit: async (value) => { setup().listLimit = value; await vue.nextTick(); },
      setRange: async (start, length, mode) => { setup().rangeStart = start; setup().rangeLength = length; if (mode) setup().previewMode = mode; await vue.nextTick(); },
      setMode: async (value) => { setup().previewMode = value; await vue.nextTick(); },
      seedValidDrafts: async () => {
        const state = setup(); state.newBucketName = 'Readonly:New'; state.uploadKey = 'Upload:Original'; state.uploadText = 'OldObjectUploadDraftMustDisappear';
        state.uploadFile = new File(['fixture'], 'ObjectFixture.bin', { type: 'application/octet-stream' }); state.metadataText = 'site=north'; state.tagsText = 'site=north';
        state.semanticText = 'Object semantic fixture'; state.semanticSearchMode = 'text'; state.semanticTopK = 1;
        state.checkedRowKeys = state.rows.map((row: { key: string }) => row.key); state.copyTargetKey = 'Copy:Original'; state.multipartKey = 'Multipart:Original';
        const session = state.multipartSessions[0]; if (session) state.resumeMultipartSession(session.upload.uploadId);
        state.multipartFile = new File(['part'], 'PartFixture.bin'); await vue.nextTick();
      },
      setToken: async (value) => { auth.apply({ ...auth.state, token: value }); await vue.nextTick(); },
      replaceApi: async () => { auth.api = vue.markRaw(clientModule.createApiClient(() => auth.state?.token ?? null)); auth.setApiBaseUrl('/'); await vue.nextTick(); },
      snapshot: () => { const state = setup(); return {
        keys: state.rows.map((row: { key: string }) => row.key), selectedKey: state.selectedKey, preview: state.previewText, policy: state.policyDraft, tags: state.selectedTagsText, hold: state.legalHoldReason,
        versions: state.versions.map((row: { versionId: string }) => row.versionId), processing: state.processingStatus?.semanticImageId ?? null,
        thumbnail: state.selectedThumbnailUrl, semantic: state.semanticSearchResult ? JSON.stringify(state.semanticSearchResult) : '', hitUrls: Object.values(state.semanticHitUrls) as string[], audit: JSON.stringify(state.auditEntries),
        multipart: state.activeMultipart?.uploadId ?? '', drafts: [state.uploadKey, state.uploadText, state.metadataText, state.tagsText, state.multipartKey].join('|'),
        pending: state.pendingOperations.length, resultRows: state.latestResult?.rows?.length ?? 0, truncated: Boolean(state.latestResult?.truncated), hasMore: state.hasMore,
      }; }, unmount: () => app.unmount(),
    };
  }, { modules, componentPath, authPath, database: east, bucket, bucketInfo });
}

async function prepare(page: Page, options: FixtureOptions = {}): Promise<Evidence> {
  const evidence: Evidence = { reads: [], writes: [], unexpected: [] }; const origin = new URL(test.info().project.use.baseURL as string).origin;
  await page.addInitScript(({ database }) => {
    localStorage.clear(); localStorage.setItem('sndb.auth', JSON.stringify({ username: 'fixture-user', token: 'fixture-token', tokenId: 'fixture-id', isSuperuser: true }));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id: 'managed-local', name: 'Managed Local', kind: 'managed-local', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: 'managed-local', activeDatabase: database }));
    const state = window as FixtureWindow; state.__wb24Urls = []; state.__wb24UrlOrigins = []; state.__wb24Revoked = []; state.__wb24Downloads = []; state.__wb24ArrayBufferSizes = [];
    const create = URL.createObjectURL.bind(URL); URL.createObjectURL = ((blob: Blob) => { const url = create(blob); state.__wb24Urls.push(url); state.__wb24UrlOrigins.push({ url, type: blob.type, size: blob.size, stack: new Error('Object URL owner').stack ?? '' }); return url; }) as typeof URL.createObjectURL;
    const revoke = URL.revokeObjectURL.bind(URL); URL.revokeObjectURL = (url) => { state.__wb24Revoked.push(url); revoke(url); };
    const read = Blob.prototype.arrayBuffer; Blob.prototype.arrayBuffer = function () { state.__wb24ArrayBufferSizes.push(this.size); return read.call(this); };
    const click = HTMLAnchorElement.prototype.click; HTMLAnchorElement.prototype.click = function () { if (this.download) state.__wb24Downloads.push(this.download); else click.call(this); };
    class QuietEventSource { static readonly CONNECTING = 0; static readonly OPEN = 1; static readonly CLOSED = 2; readonly readyState = 1; url = ''; withCredentials = false;
      onopen = null; onmessage = null; onerror = null; addEventListener() {} removeEventListener() {} dispatchEvent() { return true; } close() {} }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  }, { database: east });
  await page.route('**/*', async (route) => {
    const url = new URL(route.request().url()); const method = route.request().method();
    if (url.origin !== origin) { evidence.unexpected.push(`external ${method} ${url.origin}${url.pathname}`); return route.abort('blockedbyclient'); }
    if (!url.pathname.startsWith('/v1/') && !url.pathname.startsWith('/healthz')) return route.continue();
    const path = decodeURIComponent(url.pathname);
    if (path === '/v1/setup/status') return json(route, { needsSetup: false, serverId: 'wb24-fixture', userCount: 1, databaseCount: 2 });
    if (path.startsWith('/healthz')) return json(route, { status: 'ok', databases: 2, uptimeSeconds: 60 });
    if (path === '/v1/db') return json(route, { databases: [east, west] });
    const matched = /^\/v1\/db\/([^/]+)(.*)$/u.exec(path);
    let action: ReadAction | undefined; let database = matched?.[1] ?? east; let key = '';
    if (path === '/v1/semantic-search/status') action = 'runtime';
    if (matched && [east, west].includes(database)) {
      const suffix = matched[2];
      if (suffix === '/schema') return json(route, { measurements: [], tables: [], documentCollections: [], indexes: [] });
      if (suffix === '/kv/keyspaces') return json(route, { keyspaces: [] });
      if (suffix === '/vector/indexes' || suffix === '/fulltext/indexes') return json(route, { indexes: [] });
      if (suffix === '/mq/topics') return json(route, { topics: [] });
      if (suffix === '/graphs') return json(route, []);
      if (suffix === '/s3') action = 'buckets';
      else if (suffix.startsWith(`/s3/${bucket}`)) {
        key = suffix.slice(`/s3/${bucket}`.length).replace(/^\//u, '');
        action = url.searchParams.has('list-type') ? 'list' : url.searchParams.has('stats') ? 'stats'
          : url.searchParams.has('lifecycle') ? 'lifecycle' : url.searchParams.has('retention') ? 'retention'
          : url.searchParams.has('quota') ? 'quota' : url.searchParams.has('policy') ? 'policy'
          : url.searchParams.has('semantic') ? 'semantic-options' : url.searchParams.has('tagging') ? 'tags'
          : url.searchParams.has('legal-hold') ? 'hold' : url.searchParams.has('versions') ? 'versions'
          : url.searchParams.has('processing') ? 'processing' : url.searchParams.has('thumbnail') ? 'thumbnail'
          : url.searchParams.has('audit') ? 'audit' : url.searchParams.has('uploads') ? 'multipart'
          : key ? route.request().headers().range ? 'range' : 'download' : undefined;
      } else if (suffix.startsWith('/images/search/') || suffix.endsWith('/similar')) action = 'semantic';
      else if (suffix === '/images/WB24:Protected/content') action = 'protected';
    }
    if (action && (method === 'GET' || (action === 'semantic' && method === 'POST'))) {
      const request: ReadRequest = { action, database, key, method, query: Object.fromEntries(url.searchParams), headers: route.request().headers() };
      evidence.reads.push(request); return options.read ? options.read(route, request) : defaultRead(route, request);
    }
    if (method !== 'GET' && method !== 'HEAD') { evidence.writes.push(`${method} ${path}${url.search}`); return json(route, { code: 'wb24_fixture_write_forbidden' }, 501); }
    evidence.unexpected.push(`${method} ${path}${url.search}`); return json(route, { code: 'wb24_contract_not_mocked' }, 501);
  });
  return evidence;
}
function object(key: string, database = east, versionId = version) { return { bucket, key, versionId, contentType: 'image/png', sizeBytes: 8192, eTag: `etag:${database}`, sha256: 'fixture-sha', isDeleteMarker: false, createdUtc: now, updatedUtc: now, metadata: { source: database }, tags: { secret: oldTag } }; }
function list(request: ReadRequest, objects: ReturnType<typeof object>[], isTruncated = false, nextContinuationToken: string | null = null) { return { bucket, prefix: request.query.prefix ?? '', maxKeys: Number(request.query['max-keys'] ?? 100), continuationToken: request.query['continuation-token'] ?? '', nextContinuationToken, isTruncated, objects }; }
function multipart() { return { upload: { bucket, key: 'Multipart:Original', uploadId: 'WB24:Upload:Original', contentType: 'application/octet-stream', initiatedUtc: now, expiresUtc: '2099-10-06T00:00:00Z', metadata: {}, tags: {} }, status: 'active', parts: [{ partNumber: 1, sizeBytes: 4, eTag: 'part:Original', sha256: 'part-sha' }] }; }
async function defaultRead(route: Route, request: ReadRequest): Promise<void> {
  const { action, database, key, query } = request;
  if (action === 'buckets') return json(route, [bucketInfo]);
  if (action === 'list') return json(route, list(request, [object(`${query.prefix ?? ''}${firstKey}`, database), object(`${query.prefix ?? ''}${secondKey}`, database)].slice(0, Number(query['max-keys'] ?? 100))));
  if (action === 'stats') return json(route, { bucket, currentObjectCount: 2, currentSizeBytes: 16384, objectVersionCount: 3, objectVersionSizeBytes: 20000, deleteMarkerCount: 0, multipartUploadCount: 1, multipartPartCount: 1, multipartPartSizeBytes: 4 });
  if (action === 'lifecycle' || action === 'retention' || action === 'quota') return json(route, { bucket, updatedUtc: now });
  if (action === 'policy') return json(route, { bucket, policyJson: JSON.stringify({ secret: oldPolicy }), updatedUtc: now });
  if (action === 'semantic-options') return json(route, { bucket, asyncIngestionEnabled: true, thumbnailEnabled: true, thumbnailMaxWidth: 320, thumbnailMaxHeight: 320, thumbnailQuality: 80, updatedUtc: now });
  if (action === 'runtime') return json(route, { enabled: true, ready: true, provider: 'fixture', profile: 'fixture-profile', dimensions: 3, configuredBackend: 'fixture', effectiveBackend: 'fixture', capabilities: ['text', 'image'] });
  if (action === 'tags') return json(route, { tags: { secret: oldTag } });
  if (action === 'hold') return json(route, { bucket, key, versionId: query.versionId ?? version, enabled: true, reason: oldHold, updatedUtc: now });
  if (action === 'versions') return json(route, { bucket, key: query.key ?? null, versions: [object(query.key ?? firstKey, database), object(query.key ?? firstKey, database, 'Version:Historical')] });
  if (action === 'processing') return json(route, { jobId: 'WB24:Job', bucket, key, versionId: version, operation: 'derive', status: 'completed', semanticRequested: true, thumbnailRequested: true, attempts: 1, semanticImageId: oldSemantic, thumbnailUrl: `/v1/db/${encodeURIComponent(database)}/s3/${encodeURIComponent(bucket)}/${key.split('/').map(encodeURIComponent).join('/')}?thumbnail`, createdUtc: now, updatedUtc: now });
  if (action === 'thumbnail' || action === 'protected') return imageBlob(route);
  if (action === 'audit') return json(route, { bucket, entries: [{ id: 'WB24:Audit', action: 'read', bucket, key: firstKey, timestampUtc: now, details: { secret: oldAudit } }] });
  if (action === 'multipart') return json(route, { bucket, maxUploads: 100, isTruncated: false, uploads: [multipart()], continuationToken: '', nextContinuationToken: null });
  if (action === 'semantic') return json(route, { queryKind: 'text', profile: 'fixture-profile', backend: 'fixture', hits: [{ id: 'WB24:Protected', score: 0.9, distance: 0.1, contentType: 'image/png', sizeBytes: 4, sha256: 'fixture-sha', contentUrl: `/v1/db/${encodeURIComponent(database)}/images/WB24%3AProtected/content`, updatedUtc: now, sourceBucket: bucket, sourceKey: oldSemantic, sourceVersionId: version }] });
  if (action === 'range') return range(route, request, `${query.versionId ?? version}:${oldPayload}`);
  return route.fulfill({ status: 200, contentType: 'application/octet-stream', body: oldPayload, headers: { 'x-amz-version-id': version } });
}
async function range(route: Route, request: ReadRequest, body: string) {
  const bounds = /^bytes=(\d+)-(\d+)$/u.exec(request.headers.range)!; const start = Number(bounds[1]);
  const end = Math.min(Number(bounds[2]), start + Buffer.byteLength(body) - 1);
  await route.fulfill({ status: 206, contentType: 'application/octet-stream', body, headers: { 'content-range': `bytes ${start}-${end}/${Math.max(8192, end + 1)}`, 'x-amz-version-id': request.query.versionId ?? version } });
}
async function imageBlob(route: Route) { await route.fulfill({ status: 200, contentType: 'image/png', body: png }); }
async function json(route: Route, body: unknown, status = 200) { await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }); }
function cleanEvidence(evidence: Evidence) { expect(evidence.writes).toEqual([]); expect(evidence.unexpected).toEqual([]); }
async function history(page: Page): Promise<Array<Record<string, unknown>>> { return page.evaluate(() => JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}').entries); }
