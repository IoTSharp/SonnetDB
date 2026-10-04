import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule } from 'node:vm';
import test from 'node:test';

const source = readFileSync(new URL('../src/utils/managementExplorer.ts', import.meta.url), 'utf8');
const explorerModule = await (async () => {
  const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }));
  await module.link((specifier) => {
    throw new Error(`managementExplorer.ts should have no runtime dependency: ${specifier}`);
  });
  await module.evaluate({ timeout: 3000 });
  return module.namespace;
})();

const {
  emptyManagementInfo,
  explorerGroups,
  firstExplorerKey,
  normalizeActiveExplorerKey,
} = explorerModule;

const measurement = {
  name: 'DeviceID',
  columns: [
    { name: 'host', role: 'TAG', dataType: 'STRING' },
    { name: 'temperature', role: 'FIELD', dataType: 'DOUBLE' },
  ],
};

const table = {
  name: 'Order:Events',
  columns: [{ name: 'id', dataType: 'INT64', isPrimaryKey: true, isNullable: false, ordinal: 0 }],
  primaryKey: ['id'],
  indexes: [],
  createdUtc: '2026-10-05T00:00:00Z',
};

const documentCollection = {
  name: 'Docs:Archive',
  jsonIndexes: [],
  fullTextIndexes: [],
  createdUtc: '2026-10-05T00:00:00Z',
};

const lifecycleIndex = {
  id: 'index:Order:Events:id',
  model: 'table',
  owner: table.name,
  name: 'IX_Order_Events_Id',
  kind: 'btree',
  state: 'ready',
  includedInBackup: true,
  rebuildable: true,
  columns: ['id'],
};

const vectorIndex = {
  measurement: measurement.name,
  column: 'embedding:en',
  kind: 'hnsw',
  dimension: 3,
  metric: 'cosine',
  params: [],
};

const fullTextIndex = {
  collection: documentCollection.name,
  name: 'body:english',
  fields: ['body'],
  tokenizer: 'standard',
  documentCount: 2,
};

const mqTopic = { topic: 'Orders:Created', messageCount: 4, nextOffset: 4 };
const bucket = {
  name: 'archive:2026',
  purpose: 'backup',
  createdUtc: '2026-10-05T00:00:00Z',
  updatedUtc: '2026-10-05T00:00:00Z',
  objectCount: 2,
  totalBytes: 2048,
};
const graph = { name: 'Topology:Beta', storageId: 'g-1', recordFormatVersion: 1 };
const backupStatus = {
  backupCapable: true,
  hasRestoreManifest: true,
  segmentCount: 2,
  walFileCount: 1,
  totalBytes: 1024,
  memTablePointCount: 0,
  checkpointLsn: 10,
  nextSegmentId: 3,
};

function schema(overrides = {}) {
  return {
    measurements: [measurement],
    tables: [table],
    documentCollections: [documentCollection],
    indexes: [lifecycleIndex],
    backupStatus,
    ...overrides,
  };
}

function management(overrides = {}) {
  return {
    kvKeyspaces: ['Hot:Keys'],
    vectorIndexes: [vectorIndex],
    fullTextIndexes: [fullTextIndex],
    mqTopics: [mqTopic],
    buckets: [bucket],
    graphs: [graph],
    error: '',
    ...overrides,
  };
}

function databaseNode(name = 'alpha', overrides = {}) {
  const nodeSchema = schema(overrides.schema);
  const nodeManagement = management(overrides.management);
  return {
    name,
    meta: '',
    measurements: nodeSchema.measurements,
    tables: nodeSchema.tables ?? [],
    documents: nodeSchema.documentCollections ?? [],
    indexes: nodeSchema.indexes ?? [],
    kvKeyspaces: nodeManagement.kvKeyspaces,
    vectorIndexes: nodeManagement.vectorIndexes,
    fullTextIndexes: nodeManagement.fullTextIndexes,
    mqTopics: nodeManagement.mqTopics,
    buckets: nodeManagement.buckets,
    graphs: nodeManagement.graphs,
    backupStatus: nodeSchema.backupStatus ?? null,
    loading: false,
    error: '',
    emptyText: '',
  };
}

test('Explorer groups preserve model order and special index/backup keys', () => {
  const groups = explorerGroups(databaseNode());
  assert.deepEqual(groups.map((group) => group.key), [
    'measurements', 'tables', 'documents', 'kv', 'indexes', 'vector',
    'fulltext', 'mq', 'buckets', 'graphs', 'backup',
  ]);
  assert.deepEqual(groups.flatMap((group) => group.items.map((item) => item.key)), [
    measurement.name,
    `table:${table.name}`,
    `document:${documentCollection.name}`,
    'kv:Hot:Keys',
    lifecycleIndex.id,
    `vector:${vectorIndex.measurement}:${vectorIndex.column}`,
    `fulltext:${fullTextIndex.collection}:${fullTextIndex.name}`,
    `mq:${mqTopic.topic}`,
    `bucket:${bucket.name}`,
    `graph:${graph.name}`,
    'backup-status',
  ]);
  assert.equal(groups.find((group) => group.key === 'indexes').items[0].model, 'index');
  assert.equal(groups.find((group) => group.key === 'backup').items[0].model, 'backup');
});

test('Exact case and colon-bearing names remain addressable while case variants fall back', () => {
  const dbSchema = schema();
  const dbManagement = management();
  assert.equal(normalizeActiveExplorerKey('DeviceID', dbSchema, dbManagement), 'DeviceID');
  assert.equal(normalizeActiveExplorerKey('deviceid', dbSchema, dbManagement), 'DeviceID');
  assert.equal(normalizeActiveExplorerKey(`table:${table.name}`, dbSchema, dbManagement), `table:${table.name}`);
  assert.equal(normalizeActiveExplorerKey('table:order:events', dbSchema, dbManagement), 'DeviceID');
  assert.equal(normalizeActiveExplorerKey('kv:Hot:Keys', dbSchema, dbManagement), 'kv:Hot:Keys');
  assert.equal(normalizeActiveExplorerKey('kv:hot:keys', dbSchema, dbManagement), 'DeviceID');
  assert.equal(normalizeActiveExplorerKey(lifecycleIndex.id, dbSchema, dbManagement), lifecycleIndex.id);
  assert.equal(normalizeActiveExplorerKey('INDEX:Order:Events:id', dbSchema, dbManagement), 'DeviceID');
  assert.equal(normalizeActiveExplorerKey('backup-status', dbSchema, dbManagement), 'backup-status');
});

test('Empty selection follows the documented group precedence and keeps backup as the final fallback', () => {
  const variants = [
    [schema(), management(), measurement.name],
    [schema({ measurements: [] }), management(), `table:${table.name}`],
    [schema({ measurements: [], tables: [] }), management(), `document:${documentCollection.name}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management(), 'kv:Hot:Keys'],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [] }), `vector:${vectorIndex.measurement}:${vectorIndex.column}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [], vectorIndexes: [] }), `fulltext:${fullTextIndex.collection}:${fullTextIndex.name}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [] }), `mq:${mqTopic.topic}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [], mqTopics: [] }), `bucket:${bucket.name}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [], mqTopics: [], buckets: [] }), `graph:${graph.name}`],
    [schema({ measurements: [], tables: [], documentCollections: [] }), management({ kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [], mqTopics: [], buckets: [], graphs: [] }), 'backup-status'],
  ];
  for (const [dbSchema, dbManagement, expected] of variants) {
    assert.equal(firstExplorerKey(dbSchema, dbManagement), expected);
    assert.equal(normalizeActiveExplorerKey('', dbSchema, dbManagement), expected);
  }
});

test('Index-only schemas expose an explicit baseline gap instead of inventing a key', () => {
  const indexOnly = schema({ measurements: [], tables: [], documentCollections: [], backupStatus: null });
  const noManagement = emptyManagementInfo();
  assert.equal(normalizeActiveExplorerKey(lifecycleIndex.id, indexOnly, noManagement), lifecycleIndex.id);
  assert.equal(firstExplorerKey(indexOnly, noManagement), '');
});

test('Legacy mq:${topic} keys are database-local and require the outer database identity', () => {
  const alphaSchema = schema({
    measurements: [], tables: [], documentCollections: [], indexes: [], backupStatus: null,
  });
  const betaSchema = schema({
    measurements: [], tables: [], documentCollections: [], indexes: [], backupStatus: null,
  });
  const alphaManagement = management({
    kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [], buckets: [], graphs: [],
    mqTopics: [mqTopic],
  });
  const betaManagement = management({
    kvKeyspaces: [], vectorIndexes: [], fullTextIndexes: [], buckets: [], graphs: [],
    mqTopics: [mqTopic],
  });
  const alphaItem = explorerGroups(databaseNode('alpha', { schema: alphaSchema, management: alphaManagement }))
    .find((group) => group.key === 'mq').items[0];
  const betaItem = explorerGroups(databaseNode('beta', { schema: betaSchema, management: betaManagement }))
    .find((group) => group.key === 'mq').items[0];

  assert.equal(alphaItem.key, `mq:${mqTopic.topic}`);
  assert.equal(betaItem.key, alphaItem.key);
  assert.equal(normalizeActiveExplorerKey(alphaItem.key, alphaSchema, alphaManagement), alphaItem.key);
  assert.equal(normalizeActiveExplorerKey(alphaItem.key, betaSchema, betaManagement), betaItem.key);
  assert.equal(normalizeActiveExplorerKey(`mq:${mqTopic.topic.toLowerCase()}`, alphaSchema, alphaManagement), alphaItem.key);
  assert.equal(normalizeActiveExplorerKey(`mq:alpha:${mqTopic.topic}`, alphaSchema, alphaManagement), `mq:${mqTopic.topic}`);
  const selections = [
    { database: 'alpha', topic: alphaItem.payload.topic, key: alphaItem.key },
    { database: 'beta', topic: betaItem.payload.topic, key: betaItem.key },
  ];
  assert.deepEqual(selections.map(({ database, topic }) => `${database}/${topic}`), [
    'alpha/Orders:Created',
    'beta/Orders:Created',
  ]);
  assert.notEqual(`${selections[0].database}/${selections[0].key}`, `${selections[1].database}/${selections[1].key}`);
});
