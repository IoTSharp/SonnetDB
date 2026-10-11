import { createHash, randomBytes } from 'node:crypto';
import { lstat, readFile, realpath, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page, type Response } from '@playwright/test';

// Routed Vue -> real Vite proxy -> isolated Kestrel. API login installs an
// ordinary non-superuser session; READ and WRITE grants are stated per journey.
// MQ identity remains database + original Topic, with instance .system/mq
// persistence. Current windows/JSONL and two write terminals do not establish
// a snapshot, cross-database physical isolation, instance restart/recovery,
// single-database backup coverage, Server resource budgets or other host gates.
const database = 'wb34';
const topic = 'Telemetry.Events_Original';
const unsupportedColonTopic = 'Telemetry:Original';
const username = 'wb34_operator';
const password = 'Workbench34:OnlyLocal!';
const profileId = 'wb34-real';
const profileName = 'WB34 real local Server';
const seedCount = 151;
const consumerGroup = 'WB34Consumer_Original';
const approvedPayload = 'WB34ApprovedPublish_Original';
const approvedHeaders = { Source_Original: 'WB34ApprovedHeader_Original', Mode_Original: 'normal-ui' };
const deniedPayload = 'WB34RejectedPublishMustDisappear';
const deniedHeaders = { Source_Original: 'WB34RejectedHeaderMustDisappear' };
const mqPath = `/v1/db/${database}/mq/${topic}`;
const topicsPath = `/v1/db/${database}/mq/topics`;
const apiTimeout = 10_000;
const responseTimeout = 15_000;
let serverOrigin = '';
let evidenceRoot = '';
let evidenceBytes = 0;
let administrator: AuthIdentity;
let operator: AuthIdentity;
let seededMessages: MqMessage[] = [];
let seedTerminal: PublishBatchResponse;
let colonRejection: { code?: string; error?: string };
let approvedMessage: MqMessage;
const savedEvidence: Array<{ file: string; bytes: number; sha256: string }> = [];

interface AuthIdentity { username: string; token: string; tokenId: string; isSuperuser: boolean }
interface MqMessage { topic: string; offset: number; timestampUtc: string; headers: Record<string, string>; payload: string }
interface BrowseResponse { messages: MqMessage[] }
interface PublishResponse { topic: string; offset: number }
interface PublishBatchResponse { topic: string; offsets: number[] }
interface AckResponse { topic: string; consumerGroup: string; nextOffset: number }
interface OffsetsResponse { topic: string; nextOffset: number; consumers: Array<{ consumerGroup: string; committedOffset: number; lag: number }> }
interface StatsResponse { topic: string; messageCount: number; nextOffset: number; consumerOffsets: Record<string, number> }
interface RetentionResponse {
  topic: string; retainedStartOffset: number; retainedEndOffset: number; retainedMessages: number;
  trimmedBeforeOffset: number; trimAcknowledgedMessages: boolean; ackRetentionMinOffsetDelta: number;
}
interface BrowserRequest { path: string; body: Record<string, unknown> | null; usedSession: boolean }
interface BrowserEvidence { requests: BrowserRequest[]; overflow: boolean }
interface HistoryEntry {
  action: string; model: string; status: string; database: string; target: string; connectionId: string;
  connectionName: string; command: string; summary: string; rowCount: number; recordsAffected: number; completeness: string;
}

test.describe.configure({ mode: 'serial', retries: 0 });
test.use({ actionTimeout: apiTimeout });
test.setTimeout(120_000);

test.beforeAll(async ({ request }) => {
  const configured = process.env.SONNETDB_MQ_REAL_BASE_URL;
  if (!configured) throw new Error('SONNETDB_MQ_REAL_BASE_URL is required; use the isolated real MQ runner.');
  const url = new URL(configured);
  if (url.protocol !== 'http:' || url.hostname !== '127.0.0.1' || !url.port || url.pathname !== '/' || url.search || url.hash || url.username || url.password) {
    throw new Error('The real MQ Server must be an isolated http://127.0.0.1:port origin.');
  }
  serverOrigin = url.origin;
  await initializeEvidenceRoot();
  const setup = await apiJson<{ needsSetup: boolean; suggestedServerId: string }>(request, 'GET', '/v1/setup/status');
  expect(setup.needsSetup, 'The runner must own a fresh, uninitialized Server.').toBe(true);
  expect(setup.suggestedServerId).toBeTruthy();
  administrator = await apiJson<AuthIdentity>(request, 'POST', '/v1/setup/initialize', {
    serverId: setup.suggestedServerId, organization: 'WB34 isolated evidence', username: 'wb34_admin', password,
    bearerToken: `wb34_${randomBytes(24).toString('hex')}`,
  }, 201);
  expect(administrator).toMatchObject({ username: 'wb34_admin', isSuperuser: true });
  expect(administrator.token).toBeTruthy();
  expect(administrator.tokenId).toBeTruthy();
  await apiJson(request, 'POST', '/v1/db', { name: database }, 201, administrator.token);
  await controlSql(request, `CREATE USER ${username} WITH PASSWORD '${password}'`);
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  operator = await apiJson<AuthIdentity>(request, 'POST', '/v1/auth/login', { username, password });
  expect(operator).toMatchObject({ username, isSuperuser: false });
  expect(operator.token).toBeTruthy();
  expect(operator.tokenId).toBeTruthy();
  // The current REST name validator rejects colon-bearing Topics before the
  // Store boundary. This separately records a rejected contract, not support.
  colonRejection = await apiJson<{ code?: string; error?: string }>(request, 'POST',
    `/v1/db/${database}/mq/${unsupportedColonTopic}/publish-batch`, { messages: [{ payload: base64('WB34ColonProbe') }] }, 400, administrator.token);
  expect(colonRejection.code ?? colonRejection.error).toBe('bad_request');
  // A single real batch creates this original Topic and seeds 151 messages.
  const messages = Array.from({ length: seedCount }, (_, index) => ({ payload: base64(seedPayload(index)), headers: seedHeaders(index) }));
  seedTerminal = await apiJson<PublishBatchResponse>(request, 'POST', `${mqPath}/publish-batch`, { messages }, 201, administrator.token);
  expect(seedTerminal).toEqual({ topic, offsets: Array.from({ length: seedCount }, (_, index) => index) });
  seededMessages = (await administratorBrowse(request, 0, seedCount)).messages;
  expect(seededMessages).toHaveLength(seedCount);
  expect(seededMessages.map((message) => ({ topic: message.topic, offset: message.offset, headers: message.headers, payload: message.payload })))
    .toEqual(messages.map((message, offset) => ({ topic, offset, ...message })));
  expect(seededMessages.every((message) => typeof message.timestampUtc === 'string' && Number.isFinite(Date.parse(message.timestampUtc)))).toBe(true);
  const topics = await apiJson<{ topics: Array<{ topic: string; messageCount: number; nextOffset: number }> }>(request, 'POST', topicsPath, undefined, 200, operator.token);
  expect(topics.topics).toEqual([{ topic, messageCount: seedCount, nextOffset: seedCount }]);
});

test.afterAll(async () => {
  if (evidenceRoot && savedEvidence.length > 0) {
    await persistEvidence('evidence-manifest', { files: [...savedEvidence], totalBytes: evidenceBytes,
      limits: { files: 24, perFileBytes: 1_048_576, totalBytes: 8_388_608 }, credentialsSaved: false,
      timeoutsMs: { test: 120_000, controlApi: apiTimeout, browserResponseAndDownloadEvent: responseTimeout, productAxios: 30_000 },
      persistenceScope: 'instance', logicalScope: 'database', physicalStore: '.system/mq' });
  }
});

test('ordinary READ browses original Topic offsets0/100 with 100/51 current messages, exact timestamp/headers/Base64 JSONL and profile history', async ({ page, request }) => {
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const { evidence, initial } = await openMq(page);
  assertSeedWindow(initial, 0, 100);
  await settledMessages(page, 100);
  await assertSelectedMessage(page, initial.messages[0]);
  const firstExport = await exportJsonl(page);
  assertExport(firstExport, initial.messages);
  const firstHistory = await latestHistory(page, 'browse');
  assertBrowseHistory(firstHistory, 0, 100);

  const tailResponse = await perform(page, 'browse', () => surface(page).getByRole('button', { name: 'Next', exact: true }).click());
  expect(tailResponse.request().postDataJSON()).toEqual({ fromOffset: 100, maxCount: 100 });
  const tail = await actualJson<BrowseResponse>(tailResponse, 200);
  assertSeedWindow(tail, 100, 51);
  await settledMessages(page, 51);
  await expect(surface(page).locator('.mq-panel-head__meta').filter({ hasText: /^Offsets /u })).toHaveText('Offsets 100 - 150 · 51 messages');
  await expect(surface(page).getByRole('button', { name: 'Next', exact: true })).toBeDisabled();
  await assertSelectedMessage(page, tail.messages[0]);
  const tailExport = await exportJsonl(page);
  assertExport(tailExport, tail.messages);
  expect(tailExport).toHaveLength(51);
  expect(tailExport.length).toBeLessThan(seedCount);
  const tailHistory = await latestHistory(page, 'browse');
  assertBrowseHistory(tailHistory, 100, 51);
  expect(new URL(page.url()).searchParams.get('node')).toBe(topic);
  expect(writes(evidence)).toHaveLength(0);
  assertEvidence(evidence);
  await persistEvidence('real-mq-read-current-windows', { requests: evidence.requests, seedTerminal,
    unsupportedColonTopic: { topic: unsupportedColonTopic, status: 400, rejection: colonRejection }, initial, firstExport, firstHistory,
    tail, tailExport, tailHistory, databaseGrant: 'READ', isSuperuser: false,
    scope: 'Two separate current offset windows; JSONL preserves each current window, not all retained messages or an instance snapshot.' });
});

test('ordinary WRITE normally approves Publish and Ack once with matching terminals, administrator Browse/Offsets and original history', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence } = await openMq(page);
  await fillPublisher(page, approvedPayload, approvedHeaders);
  await stagePublish(page, evidence);
  const publishedResponse = await confirm(page, 'publish', 201);
  const published = await actualJson<PublishResponse>(publishedResponse, 201);
  expect(publishedResponse.request().postDataJSON()).toEqual({ payload: base64(approvedPayload), headers: approvedHeaders });
  expect(published).toEqual({ topic, offset: seedCount });
  expect(Number.isSafeInteger(published.offset)).toBe(true);
  const publishHistory = await latestHistory(page, 'operation');
  assertOperationHistory(publishHistory, 'success', 1);
  expect(publishHistory?.command).toBe(`MQ PUBLISH ${topic} ${Buffer.byteLength(approvedPayload, 'utf8')} bytes`);
  expect(publishHistory?.summary).toContain('1 publish');
  approvedMessage = (await administratorBrowse(request, published.offset, 1)).messages[0];
  expect(approvedMessage).toMatchObject({ topic, offset: published.offset, payload: base64(approvedPayload), headers: approvedHeaders });
  expect(Number.isFinite(Date.parse(approvedMessage.timestampUtc))).toBe(true);

  await mqTab(page, '消费者组');
  await surface(page).locator('.mq-ack-editor .n-select').click();
  const groupInput = surface(page).locator('.mq-ack-editor .n-select input');
  await groupInput.fill(consumerGroup);
  await page.locator('.n-base-select-option').getByText(consumerGroup, { exact: true }).click();
  await surface(page).getByPlaceholder('Ack offset', { exact: true }).fill('0');
  const beforeAck = writes(evidence).length;
  await surface(page).getByRole('button', { name: 'Stage ack', exact: true }).click();
  await assertApproval(page, `MQ ACK ${topic} GROUP ${consumerGroup} OFFSET 0`);
  expect(writes(evidence)).toHaveLength(beforeAck);
  const ackResponse = await confirm(page, 'ack', 200);
  const ack = await actualJson<AckResponse>(ackResponse, 200);
  expect(ackResponse.request().postDataJSON()).toEqual({ consumerGroup, offset: 0 });
  expect(ack).toEqual({ topic, consumerGroup, nextOffset: 1 });
  expect(Number.isSafeInteger(ack.nextOffset)).toBe(true);
  const ackHistory = await latestHistory(page, 'operation');
  assertOperationHistory(ackHistory, 'success', 1);
  expect(ackHistory?.command).toBe(`MQ ACK ${topic} GROUP ${consumerGroup} OFFSET 0`);
  expect(ackHistory?.summary).toContain('1 ack');
  const offsets = await apiJson<OffsetsResponse>(request, 'POST', `${mqPath}/offsets`, undefined, 200, administrator.token);
  expect(offsets).toEqual({ topic, nextOffset: seedCount + 1,
    consumers: [{ consumerGroup, committedOffset: ack.nextOffset, lag: seedCount + 1 - ack.nextOffset }] });
  const retained = await apiJson<RetentionResponse>(request, 'POST', `${mqPath}/retention`, undefined, 200, administrator.token);
  expect(retained.topic).toBe(topic);
  expect(Number.isSafeInteger(retained.retainedStartOffset) && retained.retainedStartOffset >= 0).toBe(true);
  const afterAck = await administratorBrowse(request, published.offset, 1);
  expect(afterAck.messages).toEqual([approvedMessage]);
  expect(writes(evidence).map((entry) => entry.path)).toEqual([`${mqPath}/publish`, `${mqPath}/ack`]);
  await expect(approval(page)).toHaveCount(0);
  assertEvidence(evidence);
  await persistEvidence('real-mq-approved-publish-ack', { requests: evidence.requests,
    published: { status: publishedResponse.status(), headers: safeHeaders(publishedResponse), request: publishedResponse.request().postDataJSON(), terminal: published },
    publishHistory, administratorPublishedMessage: approvedMessage,
    ack: { status: ackResponse.status(), headers: safeHeaders(ackResponse), request: ackResponse.request().postDataJSON(), terminal: ack },
    ackHistory, offsets, retained, afterAck, databaseGrant: 'WRITE', isSuperuser: false, approvalsConsumed: 2, writeRequests: 2,
    scope: 'Publish/Ack subset only. Ack passes the approved last-processed offset; retention is observed without assuming immediate trimming or a restart.' });
});

test('real REVOKE rejects an old normal Publisher approval with403 and same-token READ/Schema200 keeps payload, draft and approval locked without replay', async ({ page, request }) => {
  await controlSql(request, `GRANT WRITE ON DATABASE ${database} TO ${username}`);
  const { evidence, initial } = await openMq(page);
  const oldMessage = initial.messages.find((message) => message.offset === 1);
  expect(oldMessage).toEqual(seededMessages[1]);
  await surface(page).locator('.mq-offset-button').filter({ hasText: /^1$/u }).click();
  await assertSelectedMessage(page, oldMessage!);
  const panel = await openResult(page);
  await panel.locator('.n-tabs-tab[data-name="raw"]').click();
  await expect(panel.locator('.workbench-result-panel__result')).toContainText(seedPayload(1));
  await closeResult(page);
  await mqTab(page, '概览');
  const sampled = await perform(page, 'stats', () => surface(page).getByRole('button', { name: 'Sample', exact: true }).click());
  const stats = await actualJson<StatsResponse>(sampled, 200);
  expect(stats).toMatchObject({ topic, nextOffset: seedCount + 1, consumerOffsets: { [consumerGroup]: 1 } });
  await expect(surface(page).locator('.mq-sparkline svg path')).toHaveCount(3);
  await expect(surface(page).locator('.mq-consumer-name')).toContainText(consumerGroup);
  await fillPublisher(page, deniedPayload, deniedHeaders);
  await expect(surface(page).locator('.mq-payload-preview')).toContainText(seedPayload(1));
  await expect(surface(page).getByPlaceholder('Payload', { exact: true })).toHaveValue(deniedPayload);
  await stagePublish(page, evidence);
  await expect(surface(page).locator('.mq-file-input')).toHaveValue('');
  expect(writes(evidence)).toHaveLength(0);
  await controlSql(request, `REVOKE ON DATABASE ${database} FROM ${username}`);
  const rejected = await perform(page, 'publish', () => approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  const rejection = await actualJson<{ code?: string; error?: string }>(rejected, 403);
  expect(rejection.code ?? rejection.error).toMatch(/forbidden|permission|access_denied/iu);
  expect(rejected.request().postDataJSON()).toEqual({ payload: base64(deniedPayload), headers: deniedHeaders });
  await hiddenPermissionPayload(page);
  const rejectedHistory = await latestHistory(page, 'operation');
  assertOperationHistory(rejectedHistory, 'error', 0);
  expect(rejectedHistory?.command).toBe(`MQ PUBLISH ${topic} ${Buffer.byteLength(deniedPayload, 'utf8')} bytes`);
  expect(rejectedHistory?.summary).toContain('affected 0');
  expect(rejectedHistory?.summary).toContain('拒绝');
  const retentionAfterRejection = await apiJson<RetentionResponse>(request, 'POST', `${mqPath}/retention`, undefined, 200, administrator.token);
  expect(retentionAfterRejection.topic).toBe(topic);
  expect(Number.isSafeInteger(retentionAfterRejection.retainedStartOffset) && retentionAfterRejection.retainedStartOffset >= 0).toBe(true);
  const administratorMessages = await administratorBrowse(request, retentionAfterRejection.retainedStartOffset, 200);
  expect(administratorMessages.messages.some((message) => message.payload === base64(deniedPayload))).toBe(false);
  expect(administratorMessages.messages.find((message) => message.offset === approvedMessage.offset)).toEqual(approvedMessage);
  const afterRejection = await apiJson<OffsetsResponse>(request, 'POST', `${mqPath}/offsets`, undefined, 200, administrator.token);
  expect(afterRejection.nextOffset).toBe(seedCount + 1);
  expect(afterRejection.consumers).toEqual([{ consumerGroup, committedOffset: 1, lag: seedCount }]);

  const lockedRequests = evidence.requests.length;
  await controlSql(request, `GRANT READ ON DATABASE ${database} TO ${username}`);
  const schemaStatus = await refreshRealSchema(page);
  await hiddenPermissionPayload(page);
  await mqTab(page, '消费者组');
  await hiddenPermissionPayload(page);
  await mqTab(page, '配置');
  await hiddenPermissionPayload(page);
  await mqTab(page, '概览');
  await hiddenPermissionPayload(page);
  await mqTab(page, '消息');
  await hiddenPermissionPayload(page);
  await expect(surface(page).getByRole('button', { name: '发布测试消息', exact: true })).toBeDisabled();
  await expect(surface(page).getByTitle('导入消息文件', { exact: true })).toBeDisabled();
  await expect(surface(page).getByTitle('刷新主题', { exact: true })).toBeDisabled();
  expect(evidence.requests).toHaveLength(lockedRequests);
  expect(writes(evidence)).toHaveLength(1);
  const sameSession = await page.evaluate((token) => JSON.parse(localStorage.getItem('sndb.auth') ?? '{}').token === token, operator.token);
  expect(sameSession).toBe(true);
  assertEvidence(evidence);
  await persistEvidence('real-mq-revoked-publish-approval', { requests: evidence.requests, initial, sampledStats: stats,
    deniedStatus: rejected.status(), rejection, rejectedHistory, retentionAfterRejection, administratorMessages, afterRejection,
    databaseGrantBeforeRevoke: 'WRITE', regrant: 'READ', isSuperuser: false, schemaStatus, sameSession,
    stillLocked: true, approvalConsumed: true, writeRequests: 1, rejectedPayloadAbsentFromAdministratorBrowse: true,
    messagesHeadersMetadataTrendResultAndPublisherCleared: true, emptyFileInputAndImportButtonDisabled: true,
    scope: 'A normal Publisher approval is refused by the real Server. No file import was staged; its empty control is disabled. Same-token READ/Schema200 and section tabs do not recover or replay.' });
});

function surface(page: Page) { return page.getByTestId('workbench-mq'); }
function approval(page: Page) { return page.getByRole('dialog', { name: 'SonnetMQ staged operations' }); }
function resultPanel(page: Page) {
  return page.locator('.workbench-result-panel').filter({ has: page.locator('.workbench-result-panel__title').filter({ hasText: /^SonnetMQ operation result$/u }) });
}
function seedPayload(index: number) { return `WB34:Seed:OriginalPayload:${String(index).padStart(4, '0')}`; }
function seedHeaders(index: number) { return { Source_Original: 'WB34SeedHeader_Original', Sequence_Original: String(index) }; }
function base64(value: string) { return Buffer.from(value, 'utf8').toString('base64'); }
function writes(evidence: BrowserEvidence) { return evidence.requests.filter((entry) => ['publish', 'ack', 'publish-batch', 'nack', 'offset-reset'].includes(entry.path.slice(mqPath.length + 1))); }
function matches(action: string) {
  return (response: Response) => response.request().method() === 'POST' && decodeURIComponent(new URL(response.url()).pathname) === `${mqPath}/${action}`;
}
async function perform(page: Page, action: string, start: () => Promise<unknown>): Promise<Response> {
  const response = page.waitForResponse(matches(action), { timeout: responseTimeout });
  await start();
  return response;
}
async function mqTab(page: Page, name: string): Promise<void> {
  await surface(page).locator('.workbench-section-tabs').getByRole('button', { name, exact: true }).click();
}
async function openMq(page: Page): Promise<{ evidence: BrowserEvidence; initial: BrowseResponse }> {
  const evidence: BrowserEvidence = { requests: [], overflow: false };
  page.on('request', (request) => {
    const path = decodeURIComponent(new URL(request.url()).pathname);
    // Explorer may refresh the Topic list with Schema; this component's exact
    // selected Topic reads/writes must remain silent after refusal.
    if (request.method() !== 'POST' || !path.startsWith(`${mqPath}/`)) return;
    if (evidence.requests.length >= 64) { evidence.overflow = true; return; }
    evidence.requests.push({ path, body: request.postData() ? request.postDataJSON() as Record<string, unknown> : null,
      usedSession: request.headers().authorization === `Bearer ${operator.token}` });
  });
  await page.addInitScript(({ identity, db, id, name }) => {
    localStorage.clear();
    localStorage.setItem('sndb.auth', JSON.stringify(identity));
    localStorage.setItem('sndb.connection.library.v1', JSON.stringify({ profiles: [{ id, name, kind: 'managed-local',
      baseUrl: '/', defaultDatabase: db, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }], activeProfileId: id, activeDatabase: db }));
  }, { identity: operator, db: database, id: profileId, name: profileName });
  const received = page.waitForResponse(matches('browse'), { timeout: responseTimeout });
  await page.goto(`/admin/app/sql?${new URLSearchParams({ tool: 'mq', database, model: 'mq', node: topic })}`);
  const response = await received;
  expect(response.request().postDataJSON()).toEqual({ fromOffset: 0, maxCount: 100 });
  const initial = await actualJson<BrowseResponse>(response, 200);
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `mq:${topic}`);
  await expect(surface(page).locator('.mq-toolbar__title')).toHaveText(topic);
  await expect(surface(page).locator('.mq-toolbar__meta')).toContainText(`${database} / MQ Topics`);
  await expect(surface(page).getByTitle('刷新主题', { exact: true })).not.toHaveClass(/n-button--loading/u);
  await mqTab(page, '消息');
  await settledMessages(page, initial.messages.length);
  return { evidence, initial };
}
async function settledMessages(page: Page, count: number): Promise<void> {
  await expect(surface(page).getByRole('button', { name: '浏览', exact: true })).not.toHaveClass(/n-button--loading/u);
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(count);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'normal');
}
function assertSeedWindow(actual: BrowseResponse, from: number, count: number): void {
  expect(actual.messages).toEqual(seededMessages.slice(from, from + count));
  expect(actual.messages).toHaveLength(count);
}
async function assertSelectedMessage(page: Page, message: MqMessage): Promise<void> {
  await expect(surface(page).locator('.mq-inspector .n-tag')).toHaveText(`offset ${message.offset}`);
  await expect(surface(page).locator('.mq-headers pre')).toHaveText(JSON.stringify(message.headers, null, 2));
  await expect(surface(page).locator('.mq-payload-preview')).toHaveText(Buffer.from(message.payload, 'base64').toString('utf8'));
  await surface(page).locator('.mq-payload-tabs .n-tabs-tab[data-name="base64"]').click();
  await expect(surface(page).locator('.mq-payload-preview')).toHaveText(message.payload);
  await surface(page).locator('.mq-payload-tabs .n-tabs-tab[data-name="text"]').click();
}
async function fillPublisher(page: Page, payload: string, headers: Record<string, string>): Promise<void> {
  await surface(page).getByRole('button', { name: '发布测试消息', exact: true }).click();
  await surface(page).getByPlaceholder('Topic', { exact: true }).fill(topic);
  await surface(page).getByPlaceholder('Headers, one key=value per line', { exact: true }).fill(Object.entries(headers).map(([key, value]) => `${key}=${value}`).join('\n'));
  await surface(page).getByPlaceholder('Payload', { exact: true }).fill(payload);
}
async function assertApproval(page: Page, command: string): Promise<void> {
  await expect(approval(page)).toBeVisible();
  await expect(approval(page)).toContainText(`${profileName}: ${database} · ${topic}`);
  await expect(approval(page)).toContainText(command);
  await expect(approval(page)).toContainText('确认执行 1 项操作');
}
async function stagePublish(page: Page, evidence: BrowserEvidence): Promise<void> {
  const count = writes(evidence).length;
  const payload = await surface(page).getByPlaceholder('Payload', { exact: true }).inputValue();
  await surface(page).getByRole('button', { name: 'Stage publish', exact: true }).click();
  await assertApproval(page, `MQ PUBLISH ${topic} ${Buffer.byteLength(payload, 'utf8')} bytes`);
  expect(writes(evidence)).toHaveLength(count);
}
async function confirm(page: Page, action: string, status: number): Promise<Response> {
  const schema = waitForSchema(page);
  const response = await perform(page, action, () => approval(page).getByRole('button', { name: '确认执行 1 项操作', exact: true }).click());
  expect(response.status()).toBe(status);
  expect((await schema).status()).toBe(200);
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page).getByTitle('刷新主题', { exact: true })).not.toHaveClass(/n-button--loading/u);
  return response;
}
function assertIdentityHistory(history: HistoryEntry | undefined): void {
  expect(history).toMatchObject({ model: 'mq', database, target: topic, connectionId: profileId, connectionName: profileName });
}
function assertBrowseHistory(history: HistoryEntry | undefined, from: number, count: number): void {
  assertIdentityHistory(history);
  expect(history).toMatchObject({ action: 'browse', status: 'success', rowCount: count, recordsAffected: -1, completeness: 'partial',
    command: `MQ BROWSE ${topic} FROM ${from} LIMIT 100` });
  expect(history?.summary).toContain('当前 offset 窗口');
}
function assertOperationHistory(history: HistoryEntry | undefined, status: string, affected: number): void {
  assertIdentityHistory(history);
  expect(history).toMatchObject({ action: 'operation', status, rowCount: 1, recordsAffected: affected });
  if (status === 'success') expect(history?.completeness).toBe('complete');
}
function assertEvidence(evidence: BrowserEvidence): void {
  expect(evidence.overflow).toBe(false);
  expect(evidence.requests.every((entry) => entry.usedSession)).toBe(true);
  expect(evidence.requests.every((entry) => ['browse', 'stats', 'offsets', 'retention', 'publish', 'ack'].includes(entry.path.slice(mqPath.length + 1)))).toBe(true);
}
async function latestHistory(page: Page, action: string): Promise<HistoryEntry | undefined> {
  return page.evaluate((name) => {
    const stored = JSON.parse(localStorage.getItem('sndb.workbench.history.v1') ?? '{"entries":[]}') as { entries: HistoryEntry[] };
    return stored.entries.slice(0, 64).find((entry) => entry.action === name);
  }, action);
}
async function openResult(page: Page) {
  await surface(page).getByTestId('mq-open-result').click();
  await expect(resultPanel(page)).toBeVisible();
  return resultPanel(page);
}
async function closeResult(page: Page): Promise<void> {
  await page.locator('.n-drawer').filter({ has: resultPanel(page) }).locator('.n-drawer-header__close').click();
  await expect(resultPanel(page)).not.toBeVisible();
}
async function exportJsonl(page: Page): Promise<Array<Record<string, unknown>>> {
  const received = page.waitForEvent('download', { timeout: responseTimeout });
  await surface(page).getByTestId('mq-export-jsonl').click();
  const download = await received;
  try {
    expect(await download.failure()).toBeNull();
    expect(download.suggestedFilename()).toBe(`${database}_${topic}.jsonl`);
    const file = await download.path();
    if (!file || !isAbsolute(file)) throw new Error('The owned browser download must have an absolute file path.');
    const info = await stat(file);
    expect(info.isFile()).toBe(true);
    expect(info.size).toBeLessThanOrEqual(1_048_576);
    const content = await readFile(file, { encoding: 'utf8', signal: AbortSignal.timeout(apiTimeout) });
    const lines = content.trim().split(/\r?\n/u);
    if (lines.length > 100) throw new Error('MQ current export exceeded this 100-message window.');
    return lines.map((line) => JSON.parse(line) as Record<string, unknown>);
  } finally { await download.delete(); }
}
function assertExport(exported: Array<Record<string, unknown>>, messages: MqMessage[]): void {
  expect(exported).toEqual(messages.map((message) => ({ topic: message.topic, offset: message.offset, timestampUtc: message.timestampUtc,
    headers: message.headers, payloadBase64: message.payload })));
  expect(exported.every((row) => Buffer.from(String(row.payloadBase64), 'base64').toString('utf8') === seedPayload(Number(row.offset)))).toBe(true);
}
async function hiddenPermissionPayload(page: Page): Promise<void> {
  await expect(surface(page)).toHaveAttribute('data-database', database);
  await expect(surface(page)).toHaveAttribute('data-resource-key', `mq:${topic}`);
  await expect(surface(page)).toHaveAttribute('data-page-state', 'permission');
  await expect(surface(page).getByTestId('mq-permission')).toBeVisible();
  await expect(surface(page).locator('.mq-body')).toHaveCount(0);
  await expect(surface(page).locator('.mq-monitor')).toHaveCount(0);
  await expect(surface(page).locator('.mq-headline-stats')).toHaveCount(0);
  await expect(surface(page).locator('.mq-offset-button')).toHaveCount(0);
  await expect(surface(page).locator('.mq-payload-preview')).toHaveCount(0);
  await expect(surface(page).locator('.mq-headers')).toHaveCount(0);
  await expect(surface(page).locator('.mq-sparkline')).toHaveCount(0);
  await expect(surface(page).locator('.mq-publisher')).toHaveCount(0);
  await expect(surface(page).locator('.mq-file-input')).toHaveValue('');
  await expect(surface(page).getByTitle('导入消息文件', { exact: true })).toBeDisabled();
  await expect(resultPanel(page)).toHaveCount(0);
  await expect(approval(page)).toHaveCount(0);
  await expect(surface(page)).not.toContainText(seedPayload(1));
  await expect(surface(page)).not.toContainText(seedHeaders(1).Source_Original);
  await expect(surface(page)).not.toContainText(consumerGroup);
  await expect(surface(page)).not.toContainText(deniedPayload);
  await expect(surface(page)).not.toContainText(deniedHeaders.Source_Original);
}
function waitForSchema(page: Page): Promise<Response> {
  return page.waitForResponse((response) => response.request().method() === 'GET'
    && decodeURIComponent(new URL(response.url()).pathname) === `/v1/db/${database}/schema`, { timeout: responseTimeout });
}
async function refreshRealSchema(page: Page): Promise<number> {
  const schema = waitForSchema(page);
  await page.getByTitle('刷新资源', { exact: true }).click();
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
  expect(response.headers()['content-type']).toContain('json');
  const raw = await response.text();
  if (Buffer.byteLength(raw, 'utf8') > 262_144) throw new Error('MQ JSON evidence exceeded 256 KiB.');
  return JSON.parse(raw) as T;
}
async function administratorBrowse(request: APIRequestContext, fromOffset: number, maxCount: number): Promise<BrowseResponse> {
  return apiJson<BrowseResponse>(request, 'POST', `${mqPath}/browse`, { fromOffset, maxCount }, 200, administrator.token);
}
async function apiJson<T = Record<string, unknown>>(request: APIRequestContext, method: 'GET' | 'POST', path: string, data?: unknown, status = 200, token?: string): Promise<T> {
  const response = await request.fetch(new URL(path, serverOrigin).href, { method, data,
    headers: token ? { Authorization: `Bearer ${token}` } : {}, timeout: apiTimeout, maxRetries: 0 });
  try {
    expect(response.status(), `${method} ${path} must return ${status}`).toBe(status);
    const raw = await response.text();
    if (Buffer.byteLength(raw, 'utf8') > 262_144) throw new Error('MQ control JSON exceeded 256 KiB.');
    return JSON.parse(raw) as T;
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
  const configured = process.env.SONNETDB_MQ_REAL_EVIDENCE_ROOT;
  if (!configured || !isAbsolute(configured)) throw new Error('The shared runner must provide an absolute SONNETDB_MQ_REAL_EVIDENCE_ROOT run directory.');
  const info = await lstat(configured);
  if (!info.isDirectory() || info.isSymbolicLink()) throw new Error('The real evidence runRoot must be an existing ordinary directory.');
  const root = await realpath(configured);
  const expectedParent = resolve('D:/source/SonnetDB/artifacts/wb34-validation-20261007');
  if (!samePath(dirname(root), expectedParent) || !/^mq-real-[0-9TZ.-]+-[0-9a-f-]{36}$/u.test(basename(root))) {
    throw new Error('Evidence runRoot must be an mq-real run immediately inside the named WB34 validation artifact directory.');
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
  if (marker.runId !== basename(root) || marker.test !== 'mq-real-permission.spec.ts' || marker.baseUrl !== serverOrigin) {
    throw new Error('The real evidence marker must match this runner, spec and isolated Server.');
  }
  evidenceRoot = root;
}
async function persistEvidence(name: string, value: unknown): Promise<void> {
  if (!evidenceRoot || !/^[a-z0-9-]{1,70}$/u.test(name) || savedEvidence.length >= 24) throw new Error('MQ evidence file/path budget exceeded.');
  if (!samePath(await realpath(evidenceRoot), evidenceRoot)) throw new Error('The evidence directory identity changed.');
  const target = resolve(evidenceRoot, `${name}.json`);
  if (!samePath(dirname(target), evidenceRoot)) throw new Error('Evidence path escaped the verified runRoot.');
  const content = JSON.stringify({ recordedAtUtc: new Date().toISOString(), database, topic, ...value as Record<string, unknown> }, null, 2);
  const bytes = Buffer.byteLength(content, 'utf8');
  if (bytes > 1_048_576 || evidenceBytes + bytes > 8_388_608) throw new Error('MQ evidence exceeded 1 MiB/file or 8 MiB/run.');
  if ([password, administrator?.token, operator?.token, administrator?.tokenId, operator?.tokenId].filter(Boolean).some((secret) => content.includes(secret))) {
    throw new Error('Credential material must never be written to MQ evidence.');
  }
  await writeFile(target, content, { encoding: 'utf8', flag: 'wx', signal: AbortSignal.timeout(apiTimeout) });
  evidenceBytes += bytes;
  savedEvidence.push({ file: basename(target), bytes, sha256: createHash('sha256').update(content).digest('hex') });
}
