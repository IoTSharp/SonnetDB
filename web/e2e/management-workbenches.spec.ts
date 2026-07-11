import { resolve } from 'node:path';
import { mkdirSync } from 'node:fs';
import { expect, test, type Page, type Route } from '@playwright/test';

const database = 'factory';
const now = '2026-07-10T08:00:00Z';

const schema = {
  measurements: [
    {
      name: 'sensor_readings',
      columns: [
        { name: 'time', role: 'time', dataType: 'TIMESTAMP' },
        { name: 'device_id', role: 'tag', dataType: 'STRING' },
        { name: 'temperature', role: 'field', dataType: 'DOUBLE' },
        { name: 'embedding', role: 'field', dataType: 'VECTOR(3)' },
      ],
    },
  ],
  tables: [
    {
      name: 'orders',
      columns: [
        { name: 'id', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 },
        { name: 'status', dataType: 'STRING', isPrimaryKey: false, isNullable: false, ordinal: 1 },
        { name: 'amount', dataType: 'DOUBLE', isPrimaryKey: false, isNullable: false, ordinal: 2 },
      ],
      primaryKey: ['id'],
      indexes: [],
      foreignKeys: [],
      createdUtc: now,
    },
  ],
  documentCollections: [
    {
      name: 'device_profiles',
      jsonIndexes: [],
      fullTextIndexes: [{ name: 'profile_text', fields: ['$.name', '$.notes'], tokenizer: 'unicode', createdUtc: now, includedInBackup: true, rebuildable: true }],
      createdUtc: now,
      validator: { rules: [{ path: '$.name', required: true, type: 'string' }], validationAction: 'error' },
    },
  ],
  indexes: [],
  backupStatus: {
    backupCapable: true,
    hasRestoreManifest: false,
    segmentCount: 3,
    walFileCount: 1,
    totalBytes: 8192,
    memTablePointCount: 12,
    checkpointLsn: 42,
    nextSegmentId: 4,
  },
};

const vectorIndexes = [{
  measurement: 'sensor_readings',
  column: 'embedding',
  kind: 'hnsw',
  dimension: 3,
  metric: 'cosine',
  params: [{ key: 'm', value: '16' }],
  rowCount: 128,
}];

const fullTextIndexes = [{
  collection: 'device_profiles',
  name: 'profile_text',
  fields: ['$.name', '$.notes'],
  tokenizer: 'unicode',
  documentCount: 24,
  termCount: 310,
}];

const mqTopics = [{ topic: 'telemetry.raw', messageCount: 48, nextOffset: 48 }];
const buckets = [{ name: 'inspection-media', purpose: 'field evidence', createdUtc: now, updatedUtc: now, objectCount: 1, totalBytes: 2048 }];

test.beforeEach(async ({ page }) => {
  await page.addInitScript(() => {
    localStorage.setItem('sndb.auth', JSON.stringify({
      username: 'admin',
      token: 'e2e-token',
      tokenId: 'e2e-token-id',
      isSuperuser: true,
    }));
    localStorage.setItem('sndb.sql.console.tabs.v1', JSON.stringify({
      tabs: [{
        id: 'e2e-sql-tab',
        title: 'Operations review',
        db: 'factory',
        sql: 'SELECT * FROM sensor_readings LIMIT 100',
        results: [],
        summary: '',
        errorMsg: '',
        ranOnce: false,
        source: 'manual',
        createdAt: 1,
        updatedAt: 1,
      }],
      activeTabId: 'e2e-sql-tab',
    }));

    class QuietEventSource {
      static readonly CONNECTING = 0;
      static readonly OPEN = 1;
      static readonly CLOSED = 2;
      readonly CONNECTING = 0;
      readonly OPEN = 1;
      readonly CLOSED = 2;
      readyState = 1;
      url = '';
      withCredentials = false;
      onopen: ((event: Event) => void) | null = null;
      onmessage: ((event: MessageEvent) => void) | null = null;
      onerror: ((event: Event) => void) | null = null;
      addEventListener(): void {}
      removeEventListener(): void {}
      dispatchEvent(): boolean { return true; }
      close(): void { this.readyState = 2; }
    }
    window.EventSource = QuietEventSource as unknown as typeof EventSource;
  });

  await mockManagementContracts(page);
  await page.route('**/healthz', (route) => json(route, { status: 'ok', databaseCount: 1, uptimeSeconds: 60 }));
  await page.route('**/healthz/ready', (route) => json(route, {
    status: 'Degraded',
    totalDuration: '00:00:00.0040000',
    entries: {
      segment_store_writable: { status: 'Healthy', description: 'Segment 存储目录可写。', duration: '00:00:00.0010000', tags: ['ready', 'storage'] },
      wal_writable: { status: 'Healthy', description: 'WAL 目录可写。', duration: '00:00:00.0010000', tags: ['ready', 'storage'] },
      copilot_provider_reachable: { status: 'Degraded', description: 'chat.api_key_missing', duration: '00:00:00.0010000', tags: ['ready', 'provider'] },
      copilot_embedding_provider_reachable: { status: 'Healthy', description: '本地 provider 已就绪。', duration: '00:00:00.0010000', tags: ['ready', 'provider'] },
    },
  }));
  await page.route('**/v1/diagnostics/slow-queries*', (route) => json(route, {
    enabled: true,
    thresholdMs: 10000,
    warningThresholdMs: 30000,
    criticalThresholdMs: 60000,
    capacity: 256,
    count: 1,
    items: [{
      timestampMs: Date.parse(now),
      database,
      sql: "SELECT * FROM sensor_readings WHERE device_id = 'line-01'",
      normalizedSql: 'SELECT * FROM sensor_readings WHERE device_id = ?',
      fingerprint: '7f21e18e761adb83',
      elapsedMs: 12480.5,
      rowCount: 42,
      recordsAffected: -1,
      failed: false,
      severity: 'slow',
    }],
  }));
  await page.route('**/v1/diagnostics/top-queries*', (route) => json(route, {
    enabled: true,
    capacity: 256,
    sampleCount: 7,
    items: [{
      database,
      normalizedSql: 'SELECT * FROM sensor_readings WHERE device_id = ?',
      fingerprint: '7f21e18e761adb83',
      count: 7,
      failedCount: 1,
      p50Ms: 11200,
      p95Ms: 18600,
      maxMs: 22400,
      lastSeenTimestampMs: Date.parse(now),
    }],
  }));
});

test('top bar exposes the four readiness checks', async ({ page }) => {
  await page.goto('/admin/app/dashboard');

  const trigger = page.getByRole('button', { name: '服务就绪状态：降级' });
  await expect(trigger).toBeVisible();
  await expect(trigger.locator('.status-dot')).toHaveCount(4);
  await trigger.click();

  const details = page.getByLabel('服务就绪检查详情');
  await expect(details).toContainText('Segment 存储');
  await expect(details).toContainText('WAL');
  await expect(details).toContainText('Chat provider');
  await expect(details).toContainText('Embedding provider');
  await expect(details).toContainText('chat.api_key_missing');
});

const workbenches = [
  { tool: '', testId: 'workbench-sql', identity: 'SELECT * FROM sensor_readings' },
  { tool: 'table', testId: 'workbench-table', identity: 'orders' },
  { tool: 'document', testId: 'workbench-document', identity: 'device_profiles' },
  { tool: 'kv', testId: 'workbench-kv', identity: 'sessions' },
  { tool: 'mq', testId: 'workbench-mq', identity: 'telemetry.raw' },
  { tool: 'vector', testId: 'workbench-vector', identity: 'sensor_readings.embedding' },
  { tool: 'fulltext', testId: 'workbench-fulltext', identity: 'device_profiles.profile_text' },
  { tool: 'bucket', testId: 'workbench-bucket', identity: 'inspection-media' },
] as const;

for (const workbench of workbenches) {
  test(`${workbench.testId} renders through the shared management contracts`, async ({ page }) => {
    const query = workbench.tool ? `?tool=${workbench.tool}` : '';
    await page.goto(`/admin/app/sql${query}`);

    const surface = page.getByTestId(workbench.testId);
    await expect(surface).toBeVisible();
    await expect(surface).toContainText(workbench.identity);
    if (workbench.tool === '') {
      await page.getByTitle('查看结果').click();
      const resultDrawer = page.locator('[data-workbench-result-drawer]');
      await expect(resultDrawer).toBeVisible();
      await expect(resultDrawer).toContainText('SQL results');
    }
    if (process.env.SONNETDB_CAPTURE_M29 === '1') {
      await captureM29(page, `default-${workbench.tool || 'sql'}`);
    }
    const explorer = page.locator('.schema-sidebar');
    await expect(explorer).toContainText(database);
    if (!(await explorer.getByText('KV Keyspaces', { exact: true }).isVisible())) {
      await explorer.locator('.schema-database-node', { hasText: database }).locator('.schema-item__caret').click();
    }
    await expect(explorer).toContainText('KV Keyspaces');
    await expect(explorer).toContainText('MQ Topics');
    await expect(explorer).toContainText('Buckets');

    if (process.env.SONNETDB_CAPTURE_DOCS === '1' && workbench.tool === 'mq') {
      await page.screenshot({
        path: resolve(process.cwd(), '../docs/assets/management-workbench-mq.png'),
        fullPage: true,
      });
    }
  });
}

const taskViews = [
  { tool: 'table', testId: 'workbench-table', tab: '设计器', content: 'Create table' },
  { tool: 'document', testId: 'workbench-document', tab: 'Validator', content: 'Sample precheck' },
  { tool: 'kv', testId: 'workbench-kv', tab: '批量操作', content: 'Batch operations' },
  { tool: 'mq', testId: 'workbench-mq', tab: '消息', content: 'Message inspector' },
  { tool: 'vector', testId: 'workbench-vector', tab: '索引参数', content: 'Index parameters' },
  { tool: 'fulltext', testId: 'workbench-fulltext', tab: 'Analyzer', content: 'Analyzer preview' },
  { tool: 'bucket', testId: 'workbench-bucket', tab: '治理', content: 'Lifecycle' },
] as const;

for (const taskView of taskViews) {
  test(`${taskView.testId} opens the ${taskView.tab} task view`, async ({ page }) => {
    await page.goto(`/admin/app/sql?tool=${taskView.tool}`);
    const surface = page.getByTestId(taskView.testId);
    await surface.locator('.workbench-section-tabs').getByRole('button', { name: taskView.tab, exact: true }).click();
    await expect(surface).toContainText(taskView.content);
    if (process.env.SONNETDB_CAPTURE_M29 === '1') {
      await captureM29(page, `task-${taskView.tool}`);
    }
  });
}

test('MQ message inspector remains usable at the compact desktop breakpoint', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/admin/app/sql?tool=mq');
  const surface = page.getByTestId('workbench-mq');
  await surface.locator('.workbench-section-tabs').getByRole('button', { name: '消息', exact: true }).click();
  await expect(surface).toContainText('Message inspector');
  await expect(page.locator('html')).not.toHaveCSS('overflow-x', 'scroll');
  if (process.env.SONNETDB_CAPTURE_M29 === '1') await captureM29(page, 'responsive-mq-1280');
});

test('KV browser keeps its task navigation at the narrow desktop breakpoint', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 900 });
  await page.goto('/admin/app/sql?tool=kv');
  await page.getByTitle('收起资源浏览器').click();
  const surface = page.getByTestId('workbench-kv');
  await expect(surface.locator('.workbench-section-tabs')).toBeVisible();
  await expect(surface.locator('.workbench-section-tabs').getByRole('button', { name: '浏览器', exact: true })).toBeVisible();
  if (process.env.SONNETDB_CAPTURE_M29 === '1') await captureM29(page, 'responsive-kv-800');
});

test('staged KV write opens the shared approval modal', async ({ page }) => {
  await page.goto('/admin/app/sql?tool=kv');
  const surface = page.getByTestId('workbench-kv');
  await surface.locator('.workbench-section-tabs').getByRole('button', { name: '批量操作', exact: true }).click();
  await surface.getByRole('button', { name: 'Stage set', exact: true }).click();
  const approval = page.getByRole('dialog', { name: 'KV operation batch' });
  await expect(approval).toBeVisible();
  await expect(approval).toContainText('暂存预览');
  await expect(approval).toContainText('factory.sessions');
  await expect(approval).toContainText('entries=1');
  if (process.env.SONNETDB_CAPTURE_M29 === '1') await captureM29(page, 'shared-write-approval');
});

test('workspace header opens the filtered history drawer', async ({ page }) => {
  await page.goto('/admin/app/sql?tool=table');
  await page.getByTitle('查看工作台历史').click();
  const drawer = page.locator('.workbench-history');
  await expect(drawer).toBeVisible();
  await expect(drawer.getByPlaceholder('筛选操作、对象或命令')).toBeVisible();
  await expect(drawer).toContainText('条记录');
});

test('SQL workspace opens slow query and Top-N diagnostics', async ({ page }) => {
  await page.goto('/admin/app/sql');
  await page.getByTitle('查看慢查询与 Top-N').click();

  const drawer = page.locator('.query-diagnostics');
  await expect(drawer).toBeVisible();
  await expect(drawer).toContainText('12.48 s');
  await expect(drawer).toContainText('7f21e18e761adb83');

  await drawer.getByText('Top-N', { exact: true }).click();
  await expect(drawer).toContainText('1 个指纹 · 7 条样本');
  await expect(drawer).toContainText('18.60 s');
  await expect(drawer).toContainText('1');
});

test('Studio bridge exposes native server controls and disk connection library', async ({ page }) => {
  await mockStudioBridge(page);
  const bridgeUrl = encodeURIComponent('http://127.0.0.1:54980/studio-bridge');
  await page.goto(`/admin/app/sql?studioBridgeUrl=${bridgeUrl}&studioBridgeToken=studio-e2e-token`);

  const nativeControls = page.locator('.native-bridge-controls');
  await expect(nativeControls).toBeVisible();
  await expect(nativeControls).toContainText('Local healthy');
  await expect(nativeControls.getByRole('button', { name: 'Health' })).toBeVisible();
  await expect(nativeControls.getByRole('button', { name: 'Stop' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Studio profile' })).toBeVisible();

  await nativeControls.getByTitle('配置本地 Server').click();
  const settings = page.locator('.native-server-settings');
  await expect(settings.getByRole('textbox')).toHaveValue('C:\\SonnetDB\\Studio\\data');
  await settings.getByRole('button', { name: '浏览' }).click();
  await expect(settings.getByRole('textbox')).toHaveValue('D:\\SonnetDB\\factory-data');

  if (process.env.SONNETDB_CAPTURE_DOCS === '1') {
    await page.screenshot({
      path: resolve(process.cwd(), '../docs/assets/studio-native-bridge.png'),
      fullPage: true,
    });
  }
  if (process.env.SONNETDB_CAPTURE_M29 === '1') await captureM29(page, 'studio-bridge');
});

test('Studio desktop menu actions drive the shared SQL workbench', async ({ page }) => {
  await mockStudioBridge(page);
  const bridgeUrl = encodeURIComponent('http://127.0.0.1:54980/studio-bridge');
  await page.goto(`/admin/app/sql?studioBridgeUrl=${bridgeUrl}&studioBridgeToken=studio-e2e-token`);

  const tabs = page.getByRole('tab');
  const initialTabCount = await tabs.count();
  await dispatchStudioAction(page, 'query.new');
  await expect(tabs).toHaveCount(initialTabCount + 1);

  await dispatchStudioAction(page, 'file.open');
  await expect(page.getByRole('tab', { name: /factory-query\.sql/u })).toBeVisible();
  await expect(page.locator('.cm-content')).toContainText('SELECT * FROM factory_metrics');

  const saveRequest = page.waitForRequest((request) => request.url().endsWith('/dialogs/save-file'));
  await dispatchStudioAction(page, 'file.save');
  expect((await saveRequest).postDataJSON()).toMatchObject({
    content: 'SELECT * FROM factory_metrics LIMIT 50;',
  });

  await dispatchStudioAction(page, 'view.history');
  await expect(page.locator('.workbench-history')).toBeVisible();
});

test('connection library reports per-profile health state', async ({ page }) => {
  await page.goto('/admin/app/sql');
  await page.getByTitle('检查全部连接健康状态').click();
  await page.locator('.connection-button').click();
  await expect(page.getByText(/Managed Local.*健康.*ms/u)).toBeVisible();
});

test('object workbench restores a persisted multipart session and parts', async ({ page }) => {
  await page.goto('/admin/app/sql?tool=bucket');
  const surface = page.getByTestId('workbench-bucket');
  await surface.locator('.workbench-section-tabs').getByRole('button', { name: 'Multipart', exact: true }).click();
  await surface.locator('.n-base-selection').filter({ hasText: '选择服务器上的 Multipart 会话' }).click();
  await page.getByText(/captures\/line-1\.bin · 2 parts · active/u).click();
  await expect(surface).toContainText('mpu_e2e_resume');
  await expect(surface).toContainText('2 parts');
  await expect(surface.locator('tbody tr')).toHaveCount(2);
});

async function mockManagementContracts(page: Page): Promise<void> {
  await page.route('**/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;

    if (path === '/v1/setup/status') {
      return json(route, { needsSetup: false, suggestedServerId: 'sndb-e2e', serverId: 'sndb-e2e', organization: 'SonnetDB E2E', userCount: 1, databaseCount: 1 });
    }
    if (path === '/v1/db') return json(route, { databases: [database] });
    if (path === `/v1/db/${database}/schema`) return json(route, schema);
    if (path === `/v1/db/${database}/kv/keyspaces`) return json(route, { keyspaces: ['sessions'] });
    if (path === `/v1/db/${database}/vector/indexes`) return json(route, { indexes: vectorIndexes });
    if (path === `/v1/db/${database}/fulltext/indexes`) return json(route, { indexes: fullTextIndexes });
    if (path === `/v1/db/${database}/mq/topics`) return json(route, { topics: mqTopics });
    if (path === `/v1/db/${database}/sql`) return ndjson(route);
    if (path === `/v1/db/${database}/documents/device_profiles/find`) {
      return json(route, {
        collection: 'device_profiles',
        documents: [{ id: 'device-001', document: { name: 'Pump 01', notes: 'North station' }, version: 2 }],
        count: 1,
        limit: 100,
        skip: 0,
        continuationToken: null,
        hasMore: false,
      });
    }
    if (path === `/v1/db/${database}/documents/device_profiles/count`) return json(route, { collection: 'device_profiles', count: 1 });
    if (path === `/v1/db/${database}/kv/sessions/stats`) {
      return json(route, { totalKeys: 3, activeKeys: 3, expiredKeys: 0, expiringKeys: 1, nearestExpiresAtUtc: '2026-07-11T08:00:00Z' });
    }
    if (path === `/v1/db/${database}/kv/sessions/scan`) {
      return json(route, { entries: [{ key: 'device:001', value: 'eyJzdGF0dXMiOiJvbmxpbmUifQ==', version: 4, expiresAtUtc: null }], nextCursor: null, hasMore: false });
    }
    if (path === `/v1/db/${database}/mq/telemetry.raw/stats`) {
      return json(route, { topic: 'telemetry.raw', messageCount: 48, nextOffset: 48, consumerOffsets: { analytics: 44 } });
    }
    if (path === `/v1/db/${database}/mq/telemetry.raw/offsets`) {
      return json(route, { topic: 'telemetry.raw', nextOffset: 48, consumers: [{ consumerGroup: 'analytics', committedOffset: 44, lag: 4 }] });
    }
    if (path === `/v1/db/${database}/mq/telemetry.raw/retention`) {
      return json(route, { topic: 'telemetry.raw', retainedStartOffset: 0, retainedEndOffset: 47, retainedMessages: 48, trimmedBeforeOffset: 0, retentionMaxAgeSeconds: 86400, retentionMaxBytes: 1048576, retentionIntervalSeconds: 60, trimAcknowledgedMessages: true, ackRetentionMinOffsetDelta: 100, segmentMaxBytes: 65536, hotTailMaxBytes: 65536, segmentCacheSize: 4 });
    }
    if (path === `/v1/db/${database}/mq/telemetry.raw/browse`) {
      return json(route, { messages: [{ topic: 'telemetry.raw', offset: 47, timestampUtc: now, headers: { source: 'gateway-01' }, payload: 'eyJ0ZW1wZXJhdHVyZSI6MjMuNX0=' }] });
    }
    if (path === `/v1/db/${database}/s3` && url.search === '') return json(route, buckets);
    if (path === `/v1/db/${database}/s3/inspection-media`) return mockBucketRequest(route, url);

    return json(route, { code: 'e2e_contract_not_mocked', message: `${request.method()} ${path}${url.search}` }, 501);
  });
}

async function mockStudioBridge(page: Page): Promise<void> {
  await page.route('http://127.0.0.1:54980/studio-bridge/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    const status = { isRunning: true, startedByStudio: true, healthy: true, processId: 260, url: 'http://127.0.0.1:5080', dataRoot: 'C:\\SonnetDB\\Studio\\data', error: null };
    if (path.endsWith('/manifest')) {
      return json(route, {
        mode: 'desktop',
        version: 'e2e',
        serverUrl: 'http://127.0.0.1:5080',
        managedServerUrl: 'http://127.0.0.1:5080',
        dataRoot: status.dataRoot,
        capabilities: ['dialogs.openFile', 'dialogs.saveFile', 'connections.diskLibrary', 'server.managedLocal', 'menu.native'],
        menu: [
          { id: 'query.new', label: 'New Query', command: 'query.new', group: 'File', shortcut: 'Ctrl+N' },
          { id: 'file.open', label: 'Open SQL...', command: 'dialogs.openFile', group: 'File', shortcut: 'Ctrl+O' },
          { id: 'file.save', label: 'Save SQL As...', command: 'dialogs.saveFile', group: 'File', shortcut: 'Ctrl+S' },
        ],
        managedServer: status,
      });
    }
    if (path.endsWith('/connections')) {
      return json(route, {
        profiles: [{ id: 'studio-profile', name: 'Studio profile', kind: 'remote', baseUrl: '/', defaultDatabase: database, tokenMode: 'current-session', createdAt: 1, updatedAt: 1 }],
        activeProfileId: 'studio-profile',
        activeDatabase: database,
      });
    }
    if (path.endsWith('/server/status')) return json(route, status);
    if (path.endsWith('/dialogs/open-file')) {
      return json(route, { canceled: false, fileName: 'factory-query.sql', content: 'SELECT * FROM factory_metrics LIMIT 50;', error: null });
    }
    if (path.endsWith('/dialogs/save-file')) {
      return json(route, { canceled: false, fileName: 'D:\\Queries\\factory-query.sql', error: null });
    }
    if (path.endsWith('/dialogs/select-directory')) {
      return json(route, { canceled: false, path: 'D:\\SonnetDB\\factory-data', error: null });
    }
    return json(route, status);
  });
}

async function dispatchStudioAction(page: Page, id: string): Promise<void> {
  await page.evaluate((actionId) => {
    window.dispatchEvent(new CustomEvent('nativeWeb:studio.desktop-action', { detail: { id: actionId } }));
  }, id);
}

async function mockBucketRequest(route: Route, url: URL): Promise<void> {
  if (url.searchParams.has('uploads')) {
    return json(route, {
      bucket: 'inspection-media',
      maxUploads: 100,
      continuationToken: null,
      nextContinuationToken: null,
      isTruncated: false,
      uploads: [{
        upload: { bucket: 'inspection-media', key: 'captures/line-1.bin', uploadId: 'mpu_e2e_resume', contentType: 'application/octet-stream', initiatedUtc: now, expiresUtc: '2099-01-01T00:00:00Z', metadata: {}, tags: {} },
        status: 'active',
        parts: [
          { partNumber: 1, sizeBytes: 1024, eTag: 'etag-1', sha256: 'sha-1' },
          { partNumber: 2, sizeBytes: 2048, eTag: 'etag-2', sha256: 'sha-2' },
        ],
      }],
    });
  }
  if (url.searchParams.has('stats')) {
    return json(route, { bucket: 'inspection-media', currentObjectCount: 1, currentSizeBytes: 2048, objectVersionCount: 1, objectVersionSizeBytes: 2048, deleteMarkerCount: 0, multipartUploadCount: 0, multipartPartCount: 0, multipartPartSizeBytes: 0, quotaMaxSizeBytes: 10485760, quotaMaxObjectVersions: 1000, quotaRemainingSizeBytes: 10483712, quotaRemainingObjectVersions: 999 });
  }
  if (url.searchParams.has('lifecycle')) return json(route, { bucket: 'inspection-media', expireCurrentAfterDays: 90, expireNoncurrentAfterDays: 30, expireDeleteMarkerAfterDays: 7, updatedUtc: now });
  if (url.searchParams.has('retention')) return json(route, { bucket: 'inspection-media', retainCurrentForDays: 30, retainNoncurrentForDays: 7, updatedUtc: now });
  if (url.searchParams.has('quota')) return json(route, { bucket: 'inspection-media', maxSizeBytes: 10485760, maxObjectVersions: 1000, updatedUtc: now });
  if (url.searchParams.has('policy')) return json(route, { bucket: 'inspection-media', policyJson: null, updatedUtc: now });
  if (url.searchParams.has('versions')) return json(route, { bucket: 'inspection-media', key: null, versions: [] });
  if (url.searchParams.has('audit')) return json(route, { bucket: 'inspection-media', entries: [] });
  return json(route, {
    bucket: 'inspection-media',
    prefix: '',
    maxKeys: 100,
    continuationToken: null,
    nextContinuationToken: null,
    isTruncated: false,
    objects: [],
  });
}

async function json(route: Route, body: unknown, status = 200): Promise<void> {
  await route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function ndjson(route: Route): Promise<void> {
  await route.fulfill({
    status: 200,
    contentType: 'application/x-ndjson',
    body: [
      JSON.stringify({ type: 'meta', columns: ['id', 'status', 'amount'] }),
      JSON.stringify([1001, 'ready', 42.5]),
      JSON.stringify({ type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 1.2 }),
    ].join('\n'),
  });
}

async function captureM29(page: Page, name: string): Promise<void> {
  const directory = resolve(process.cwd(), '../output/playwright/m29-implementation');
  mkdirSync(directory, { recursive: true });
  await page.screenshot({ path: resolve(directory, `${name}.png`), fullPage: true });
}
