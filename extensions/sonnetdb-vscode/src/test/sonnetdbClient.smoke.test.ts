import assert from 'node:assert/strict';
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http';
import type { AddressInfo } from 'node:net';
import { test } from 'node:test';
import { SonnetDbClient } from '../core/sonnetdbClient';

test('SonnetDbClient consumes the shared multi-model management contracts', async (context) => {
  const requests: string[] = [];
  const server = createServer((request, response) => handleRequest(request, response, requests));
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  context.after(() => new Promise<void>((resolve, reject) => {
    server.close((error) => error ? reject(error) : resolve());
  }));

  const address = server.address() as AddressInfo;
  const client = new SonnetDbClient(`http://127.0.0.1:${address.port}`, 'smoke-token');

  assert.equal((await client.checkHealth()).status, 'ok');
  assert.equal((await client.fetchSetupStatus()).needsSetup, false);
  assert.deepEqual(await client.listDatabases(), { databases: ['factory'] });
  assert.equal((await client.fetchSchema('factory')).tables?.[0]?.name, 'orders');
  assert.equal((await client.findDocuments('factory', 'device_profiles', {
    filter: { path: '$.status', op: 'eq', value: 'online' },
    limit: 25,
  })).documents[0]?.id, 'device-001');
  assert.deepEqual(await client.fetchKvKeyspaces('factory'), ['sessions']);
  assert.equal((await client.scanKvEntries('factory', 'sessions')).entries[0]?.key, 'device:001');
  assert.equal((await client.fetchVectorIndexes('factory'))[0]?.measurement, 'sensor_readings');
  assert.equal((await client.searchVectorPreview('factory', {
    measurement: 'sensor_readings',
    column: 'embedding',
    query: [0, 0, 0],
  })).hits.length, 1);
  assert.equal((await client.fetchFullTextIndexes('factory'))[0]?.name, 'profile_text');
  assert.equal((await client.searchFullTextPreview('factory', {
    collection: 'device_profiles',
    index: 'profile_text',
    field: '*',
    query: 'pump',
  })).hits[0]?.documentId, 'device-001');
  assert.equal((await client.analyzeFullText('factory', { tokenizer: 'unicode', text: 'pump alarm' })).tokens.length, 2);
  assert.equal((await client.fetchMqTopics('factory'))[0]?.topic, 'telemetry.raw');
  assert.equal((await client.browseMqMessages('factory', 'telemetry.raw')).messages[0]?.offset, 47);
  assert.equal((await client.fetchMqMonitor('factory', 'telemetry.raw')).consumers[0]?.lag, 2);
  assert.equal((await client.fetchObjectBuckets('factory'))[0]?.name, 'raw-data');
  assert.equal((await client.listObjects('factory', 'raw-data', { prefix: 'images/', maxKeys: 25 })).objects[0]?.key, 'images/pump-01.jpg');

  const sql = await client.executeSql('factory', 'SELECT * FROM sensor_readings LIMIT 1');
  assert.deepEqual(sql.columns, ['device_id', 'temperature']);
  assert.deepEqual(sql.rows, [['pump-01', 23.5]]);
  assert.equal(sql.end?.rowCount, 1);
  assert.equal((await client.ingestBulk('factory', 'sensor_readings', 'lp', Buffer.from('value=1 1'))).written, 1);

  assert.equal(requests.length, 19);
  assert.ok(requests.every((entry) => entry.endsWith('Bearer smoke-token')));
});

function handleRequest(request: IncomingMessage, response: ServerResponse, requests: string[]): void {
  const path = new URL(request.url ?? '/', 'http://127.0.0.1').pathname;
  requests.push(`${request.method} ${path} ${request.headers.authorization ?? ''}`);

  if (path === '/healthz') return writeJson(response, { status: 'ok', databases: 1, uptimeSeconds: 10, copilotEnabled: true, copilotReady: true });
  if (path === '/v1/setup/status') return writeJson(response, { needsSetup: false, suggestedServerId: 'factory', serverId: 'factory', organization: 'test', userCount: 1, databaseCount: 1 });
  if (path === '/v1/db') return writeJson(response, { databases: ['factory'] });
  if (path === '/v1/db/factory/schema') {
    return writeJson(response, {
      measurements: [{ name: 'sensor_readings', columns: [] }],
      tables: [{ name: 'orders', columns: [], primaryKey: [], indexes: [], createdUtc: '2026-07-10T08:00:00Z' }],
      documentCollections: [],
      indexes: [],
    });
  }
  if (path === '/v1/db/factory/documents/device_profiles/find') {
    return writeJson(response, {
      collection: 'device_profiles',
      documents: [{ id: 'device-001', document: { status: 'online' }, version: 4 }],
      count: 1,
      limit: 25,
      skip: 0,
      continuationToken: null,
      hasMore: false,
      batchSize: 1,
      snapshotVersion: 4,
      cursorExpiresAtUtc: null,
    });
  }
  if (path === '/v1/db/factory/kv/keyspaces') return writeJson(response, { keyspaces: ['sessions'] });
  if (path === '/v1/db/factory/kv/sessions/scan') {
    return writeJson(response, { entries: [{ key: 'device:001', value: 'b25saW5l', version: 1 }], nextCursor: null, hasMore: false });
  }
  if (path === '/v1/db/factory/vector/indexes') {
    return writeJson(response, { indexes: [{ measurement: 'sensor_readings', column: 'embedding', kind: 'hnsw', dimension: 3, metric: 'cosine', params: [], rowCount: 1 }] });
  }
  if (path === '/v1/db/factory/vector/search-preview') {
    return writeJson(response, { hits: [{ timestampUtc: 1_788_000_000_000, distance: 0.01, tags: [], fields: [] }] });
  }
  if (path === '/v1/db/factory/fulltext/indexes') {
    return writeJson(response, { indexes: [{ collection: 'device_profiles', name: 'profile_text', fields: ['$.name'], tokenizer: 'unicode', documentCount: 1, termCount: 2 }] });
  }
  if (path === '/v1/db/factory/fulltext/search-preview') {
    return writeJson(response, { hits: [{ documentId: 'device-001', score: 1.5 }] });
  }
  if (path === '/v1/db/factory/fulltext/analyze') {
    return writeJson(response, { tokens: [
      { text: 'pump', startOffset: 0, endOffset: 4, positionIncrement: 1 },
      { text: 'alarm', startOffset: 5, endOffset: 10, positionIncrement: 1 },
    ] });
  }
  if (path === '/v1/db/factory/mq/topics') {
    return writeJson(response, { topics: [{ topic: 'telemetry.raw', messageCount: 48, nextOffset: 48 }] });
  }
  if (path === '/v1/db/factory/mq/telemetry.raw/browse') {
    return writeJson(response, { messages: [{ topic: 'telemetry.raw', offset: 47, timestampUtc: '2026-07-10T08:00:00Z', headers: {}, payload: 'e30=' }] });
  }
  if (path === '/v1/db/factory/mq/telemetry.raw/monitor') {
    return writeJson(response, {
      topic: 'telemetry.raw',
      messageCount: 48,
      nextOffset: 48,
      retainedStartOffset: 0,
      consumers: [{ consumerGroup: 'ingest', committedOffset: 46, lag: 2, progressRatio: 0.96, status: 'healthy' }],
      retention: { retentionIntervalMilliseconds: 60000, trimAcknowledgedMessages: true, ackRetentionMinOffsetDelta: 100, segmentMaxBytes: 1048576, hotTailMaxBytes: 1048576, segmentCacheSize: 4 },
      deadLetter: { mode: 'disabled', candidateTopics: [] },
    });
  }
  if (path === '/v1/db/factory/s3') {
    return writeJson(response, [{ name: 'raw-data', purpose: 'source media', createdUtc: '2026-07-10T08:00:00Z', updatedUtc: '2026-07-10T08:00:00Z' }]);
  }
  if (path === '/v1/db/factory/s3/raw-data') {
    return writeJson(response, {
      bucket: 'raw-data',
      prefix: 'images/',
      maxKeys: 25,
      isTruncated: false,
      objects: [{
        bucket: 'raw-data',
        key: 'images/pump-01.jpg',
        versionId: 'v1',
        contentType: 'image/jpeg',
        sizeBytes: 1024,
        eTag: 'etag-1',
        sha256: 'sha-1',
        isDeleteMarker: false,
        createdUtc: '2026-07-10T08:00:00Z',
        updatedUtc: '2026-07-10T08:00:00Z',
        metadata: {},
        tags: { site: 'north' },
      }],
    });
  }
  if (path === '/v1/db/factory/sql') {
    response.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
    response.end([
      JSON.stringify({ type: 'meta', columns: ['device_id', 'temperature'] }),
      JSON.stringify(['pump-01', 23.5]),
      JSON.stringify({ type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 0.4 }),
    ].join('\n'));
    return;
  }
  if (path === '/v1/db/factory/measurements/sensor_readings/lp') {
    return writeJson(response, { written: 1, skipped: 0, elapsedMilliseconds: 0.3 });
  }

  writeJson(response, { code: 'not_found', message: path }, 404);
}

function writeJson(response: ServerResponse, body: unknown, status = 200): void {
  response.writeHead(status, { 'Content-Type': 'application/json' });
  response.end(JSON.stringify(body));
}
