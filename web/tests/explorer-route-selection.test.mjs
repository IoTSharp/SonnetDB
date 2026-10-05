import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule } from 'node:vm';
import test from 'node:test';

const source = readFileSync(new URL('../src/utils/managementExplorer.ts', import.meta.url), 'utf8');
const viewSource = readFileSync(new URL('../src/views/SqlConsoleView.vue', import.meta.url), 'utf8');
const descriptorSource = readFileSync(new URL('../src/management-core/resourceDescriptor.ts', import.meta.url), 'utf8');
const explorerModule = await (async () => {
  const descriptorModule = new SourceTextModule(stripTypeScriptTypes(descriptorSource, { mode: 'transform' }), {
    identifier: 'resourceDescriptor.ts',
  });
  await descriptorModule.link(() => {
    throw new Error('resourceDescriptor.ts should have no runtime dependency');
  });
  await descriptorModule.evaluate({ timeout: 3000 });

  const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }), {
    identifier: 'managementExplorer.ts',
  });
  await module.link((specifier) => {
    if (specifier === '@/management-core/resourceDescriptor') return descriptorModule;
    throw new Error(`unexpected managementExplorer.ts dependency: ${specifier}`);
  });
  await module.evaluate({ timeout: 3000 });
  return module.namespace;
})();

const { emptyManagementInfo, explorerGroups, explorerKeyFromRoute, firstExplorerKey } = explorerModule;

const measurement = {
  name: 'Device:ID',
  columns: [{ name: 'value', role: 'FIELD', dataType: 'STRING' }],
};
const table = {
  name: 'Orders:Events',
  columns: [],
  primaryKey: [],
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
  id: 'index:Orders:Events:id',
  model: 'table',
  owner: table.name,
  name: 'IX:Orders',
  kind: 'btree',
  state: 'ready',
  includedInBackup: true,
  rebuildable: true,
  columns: [],
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
  documentCount: 1,
};
const mqTopic = { topic: 'Orders:Created', messageCount: 1, nextOffset: 1 };
const bucket = {
  name: 'archive:2026',
  purpose: 'backup',
  createdUtc: '2026-10-05T00:00:00Z',
  updatedUtc: '2026-10-05T00:00:00Z',
  objectCount: 1,
  totalBytes: 10,
};
const graph = { name: 'Topology:Beta', storageId: 'g-1', recordFormatVersion: 1 };
const backupStatus = {
  backupCapable: true,
  hasRestoreManifest: true,
  segmentCount: 1,
  walFileCount: 1,
  totalBytes: 1,
  memTablePointCount: 0,
  checkpointLsn: 1,
  nextSegmentId: 2,
};

const dbSchema = {
  measurements: [measurement],
  tables: [table],
  documentCollections: [documentCollection],
  indexes: [lifecycleIndex],
  backupStatus,
};
const management = {
  ...emptyManagementInfo(),
  kvKeyspaces: ['Hot:Keys'],
  vectorIndexes: [vectorIndex],
  fullTextIndexes: [fullTextIndex],
  mqTopics: [mqTopic],
  buckets: [bucket],
  graphs: [graph],
};

test('legacy model/node routes map every Explorer model to its database-local key', () => {
  const cases = [
    ['measurement', measurement.name, measurement.name],
    ['table', table.name, `table:${table.name}`],
    ['document', documentCollection.name, `document:${documentCollection.name}`],
    ['kv', 'Hot:Keys', 'kv:Hot:Keys'],
    ['mq', mqTopic.topic, `mq:${mqTopic.topic}`],
    ['vector', `${vectorIndex.measurement}.${vectorIndex.column}`, `vector:${vectorIndex.measurement}:${vectorIndex.column}`],
    ['fulltext', `${fullTextIndex.collection}.${fullTextIndex.name}`, `fulltext:${fullTextIndex.collection}:${fullTextIndex.name}`],
    ['bucket', bucket.name, `bucket:${bucket.name}`],
    ['graph', graph.name, `graph:${graph.name}`],
    ['index', lifecycleIndex.name, lifecycleIndex.id],
    ['backup', 'Backup status', 'backup-status'],
  ];

  for (const [model, node, expected] of cases) {
    assert.equal(explorerKeyFromRoute(model, node, dbSchema, management), expected, `${model} should select ${expected}`);
  }
});

test('route selection preserves exact case and colon-bearing names', () => {
  assert.equal(explorerKeyFromRoute('measurement', 'device:id', dbSchema, management), firstExplorerKey(dbSchema, management));
  assert.equal(explorerKeyFromRoute('table', 'Orders:Events', dbSchema, management), 'table:Orders:Events');
  assert.equal(explorerKeyFromRoute('mq', 'Orders:Created', dbSchema, management), 'mq:Orders:Created');
  assert.equal(explorerKeyFromRoute('mq', 'mq:Orders:Created', dbSchema, management), 'mq:Orders:Created');
  assert.equal(explorerKeyFromRoute('vector', 'Device:ID.embedding:en', dbSchema, management), 'vector:Device:ID:embedding:en');
});

test('missing and unknown nodes use the existing first-item fallback', () => {
  const expected = firstExplorerKey(dbSchema, management);
  assert.equal(explorerKeyFromRoute('measurement', undefined, dbSchema, management), expected);
  assert.equal(explorerKeyFromRoute('measurement', 'missing', dbSchema, management), expected);
  assert.equal(explorerKeyFromRoute('unknown', 'missing', dbSchema, management), expected);
  assert.equal(explorerKeyFromRoute(undefined, undefined, dbSchema, management), expected);
});

test('MQ selection remains database-local and Graph resources stay Beta', () => {
  const alpha = explorerKeyFromRoute('mq', mqTopic.topic, dbSchema, management);
  const beta = explorerKeyFromRoute('mq', mqTopic.topic, dbSchema, management);
  assert.equal(alpha, beta);
  const graphItem = explorerGroups({
    name: 'alpha',
    meta: '',
    measurements: dbSchema.measurements,
    tables: dbSchema.tables,
    documents: dbSchema.documentCollections,
    indexes: dbSchema.indexes,
    kvKeyspaces: management.kvKeyspaces,
    vectorIndexes: management.vectorIndexes,
    fullTextIndexes: management.fullTextIndexes,
    mqTopics: management.mqTopics,
    buckets: management.buckets,
    graphs: management.graphs,
    backupStatus,
    loading: false,
    error: '',
    emptyText: '',
  }).find((group) => group.key === 'graphs').items[0];
  assert.equal(graphItem.resource.beta, true);
  assert.equal(graphItem.resource.stability, 'beta');
});

test('SQL view keeps tool-only model compatibility and waits for loaded metadata', () => {
  assert.match(viewSource, /\(\) => route\.query\.tool/);
  assert.match(viewSource, /rawModel \?\? \(tool && \[/);
  assert.match(viewSource, /!dbSchema \|\| !management/);
  assert.match(viewSource, /explorerKeyFromRoute\(model, node, dbSchema, management\)/);
  assert.ok(
    viewSource.indexOf('const routeSelectionToken') < viewSource.indexOf('watch([activeWorkbenchTool'),
    'route selection must run before object tab projection so direct URLs do not create a default tab first',
  );
  assert.doesNotMatch(viewSource, /run\(\)|confirmPreview\(\)|executeQuery/);
});
