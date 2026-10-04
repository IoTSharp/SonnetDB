import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { stripTypeScriptTypes } from 'node:module';
import { fileURLToPath } from 'node:url';
import { SourceTextModule } from 'node:vm';
import test from 'node:test';

const moduleCache = new Map();

async function loadTypeScriptModule(filePath) {
  const absolutePath = resolve(filePath);
  const cached = moduleCache.get(absolutePath);
  if (cached) return cached;
  const source = readFileSync(absolutePath, 'utf8');
  const module = new SourceTextModule(stripTypeScriptTypes(source, { mode: 'transform' }), {
    identifier: absolutePath,
  });
  moduleCache.set(absolutePath, module);
  await module.link(async (specifier, referencingModule) => {
    if (!specifier.startsWith('.')) throw new Error(`Unexpected runtime dependency: ${specifier}`);
    const dependencyPath = resolve(dirname(referencingModule.identifier), specifier);
    return loadTypeScriptModule(dependencyPath.endsWith('.ts') ? dependencyPath : `${dependencyPath}.ts`);
  });
  await module.evaluate({ timeout: 3000 });
  return module;
}

const managementCore = await loadTypeScriptModule(fileURLToPath(new URL('../src/management-core/index.ts', import.meta.url)));
const {
  CapabilityRegistry,
  createGraphResourceDescriptor,
  createMqResourceDescriptor,
  createMqResourceIdentity,
  createResourceIdentity,
} = managementCore.namespace;

test('MQ identity keeps database, topic, legacy key, and original spelling', () => {
  const identity = createMqResourceIdentity('Telemetry', 'Orders:Created');
  assert.equal(identity.database, 'Telemetry');
  assert.equal(identity.topic, 'Orders:Created');
  assert.equal(identity.name, 'Orders:Created');
  assert.equal(identity.key, 'mq:Orders:Created');
  assert.equal(identity.legacyKey, 'mq:Orders:Created');
});

test('generic MQ identities require topic and keep name aligned', () => {
  assert.throws(() => createResourceIdentity({
    database: 'Telemetry',
    model: 'mq',
    name: 'Orders:Created',
  }), /topic/);
  assert.throws(() => createResourceIdentity({
    database: 'Telemetry',
    model: 'mq',
    name: 'Orders:Created',
    topic: 'Other:Topic',
  }), /name must equal topic/);
});

test('MQ descriptor separates database logic scope from instance persistence', () => {
  const descriptor = createMqResourceDescriptor('Telemetry', 'Orders:Created');
  assert.equal(descriptor.scope, 'database');
  assert.equal(descriptor.persistenceScope, 'instance');
  assert.equal(descriptor.persistence.scope, 'instance');
  assert.equal(descriptor.persistence.path, '.system/mq');
  assert.equal(descriptor.persistence.shared, true);
  assert.equal(descriptor.persistence.includedInDatabaseBackup, false);
});

test('resource keys preserve case, colons, and special index/backup routes', () => {
  const table = createResourceIdentity({
    database: 'Telemetry',
    model: 'table',
    name: 'Order:Events',
  });
  assert.equal(table.name, 'Order:Events');
  assert.equal(table.key, 'table:Order:Events');

  const lifecycleIndex = createResourceIdentity({
    database: 'Telemetry',
    model: 'index',
    name: 'index:Order:Events:id',
    key: 'index:Order:Events:id',
  });
  assert.equal(lifecycleIndex.key, 'index:Order:Events:id');
  assert.equal(lifecycleIndex.legacyKey, 'index:Order:Events:id');

  const backup = createResourceIdentity({
    database: 'Telemetry',
    model: 'backup',
    name: 'backup-status',
    key: 'backup-status',
  });
  assert.equal(backup.key, 'backup-status');
});

test('Graph descriptors are explicitly Beta', () => {
  const graph = createGraphResourceDescriptor('Telemetry', 'Topology:Beta');
  assert.equal(graph.identity.model, 'graph');
  assert.equal(graph.stability, 'beta');
  assert.equal(graph.beta, true);
  assert.equal(graph.key, 'graph:Topology:Beta');
});

test('capability registry resolves stable ids, unknown ids, and immutable snapshots', () => {
  const registry = new CapabilityRegistry([
    { id: 'sql.query.v1', state: 'existing', label: 'SQL query' },
    { id: 'graph.write.v1', state: 'planned' },
  ]);
  assert.equal(registry.get('sql.query.v1').state, 'existing');
  assert.equal(registry.resolve('graph.write.v1').state, 'planned');

  const unknown = registry.resolve('future.capability.v1');
  assert.equal(unknown.id, 'future.capability.v1');
  assert.equal(unknown.state, 'unavailable');
  assert.equal(unknown.known, false);

  const snapshot = registry.snapshot();
  assert.deepEqual(snapshot.map((capability) => capability.id), ['sql.query.v1', 'graph.write.v1']);
  registry.register({ id: 'new.extension.v1', state: 'extension' });
  assert.equal(snapshot.length, 2);
  assert.equal(registry.size, 3);
  assert.throws(() => snapshot.push(unknown), TypeError);
  assert.throws(() => { snapshot[0].state = 'planned'; }, TypeError);
});
