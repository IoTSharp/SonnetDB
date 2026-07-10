import { resolve } from 'node:path';
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
});

const workbenches = [
  { tool: '', testId: 'workbench-sql', identity: 'SQL results' },
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
    const explorer = page.locator('.schema-sidebar');
    await expect(explorer).toContainText(database);
    await explorer.locator('.schema-database-node', { hasText: database }).locator('.schema-item__caret').click();
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

  if (process.env.SONNETDB_CAPTURE_DOCS === '1') {
    await page.screenshot({
      path: resolve(process.cwd(), '../docs/assets/studio-native-bridge.png'),
      fullPage: true,
    });
  }
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
        capabilities: ['dialogs.openFile', 'dialogs.saveFile', 'connections.diskLibrary', 'server.managedLocal'],
        menu: [],
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
    return json(route, status);
  });
}

async function mockBucketRequest(route: Route, url: URL): Promise<void> {
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
