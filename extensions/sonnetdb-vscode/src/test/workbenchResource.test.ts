import assert from 'node:assert/strict';
import test from 'node:test';
import { buildWorkbenchUrl, workbenchTargetFromNode } from '../core/workbenchResource';
import type { SonnetDbConnectionProfile } from '../core/types';
import type { TreeNode } from '../tree/sonnetdbTreeDataProvider';

const profile: SonnetDbConnectionProfile = {
  id: 'remote-one', label: 'Remote', kind: 'remote', baseUrl: 'https://sonnet.example',
  defaultDatabase: 'OtherDB', tokenSecretKey: 'private-secret-storage-key',
};
const database = 'FactoryDB:East';
const name = 'DeviceID:Main';
const base = { profile, database };
const fixtures: Array<{ node: TreeNode; model: string; name: string; key: string }> = [
  { node: { ...base, kind: 'measurement', measurement: { name, columns: [] } }, model: 'measurement', name, key: name },
  { node: { ...base, kind: 'table', table: { name, columns: [], primaryKey: [], indexes: [], createdUtc: '' } }, model: 'table', name, key: `table:${name}` },
  { node: { ...base, kind: 'document', collection: { name, jsonIndexes: [], fullTextIndexes: [], createdUtc: '' } }, model: 'document', name, key: `document:${name}` },
  { node: { ...base, kind: 'kvKeyspace', keyspace: name }, model: 'kv', name, key: `kv:${name}` },
  { node: { ...base, kind: 'vectorIndex', index: { measurement: name, column: 'Embedding:V2', kind: 'flat', metric: 'cosine', params: [] } }, model: 'vector', name: `${name}.Embedding:V2`, key: `vector:${name}:Embedding:V2` },
  { node: { ...base, kind: 'fullTextIndex', index: { collection: name, name: 'Text:V2', fields: [], tokenizer: 'standard', documentCount: 0 } }, model: 'fulltext', name: `${name}.Text:V2`, key: `fulltext:${name}:Text:V2` },
  { node: { ...base, kind: 'mqTopic', topic: { topic: name, messageCount: 0, nextOffset: 0 } }, model: 'mq', name, key: `mq:${name}` },
  { node: { ...base, kind: 'objectBucket', bucket: { name, purpose: '', createdUtc: '', updatedUtc: '' } }, model: 'bucket', name, key: `bucket:${name}` },
  { node: { ...base, kind: 'graph', graph: { name, storageId: 'graph-storage', recordFormatVersion: 1 } }, model: 'graph', name, key: `graph:${name}` },
  { node: { ...base, kind: 'index', index: { id: `table:${name}:IX:Device`, model: 'table', owner: name, name: 'IX:Device', kind: 'btree', state: 'ready', includedInBackup: true, rebuildable: true, columns: [] } }, model: 'index', name: 'IX:Device', key: `table:${name}:IX:Device` },
  { node: { ...base, kind: 'backup', backupStatus: null }, model: 'backup', name: 'backup-status', key: 'backup-status' },
];

test('workbenchTargetFromNode preserves all nine models and lifecycle legacy keys', { timeout: 1000 }, () => {
  assert.equal(fixtures.length, 11);
  for (const fixture of fixtures) {
    const target = workbenchTargetFromNode(fixture.node)!;
    assert.equal(target.database, database);
    assert.equal(target.resource!.database, database);
    assert.equal(target.resource!.model, fixture.model);
    assert.equal(target.resource!.name, fixture.name);
    assert.equal(target.resource!.key, fixture.key);
    assert.equal(target.resource!.legacyKey, fixture.key);
    assert.equal(Object.isFrozen(target), true);
    assert.equal(Object.isFrozen(target.resource), true);
  }
});

test('MQ identity keeps database and topic while recording its instance persistence boundary', { timeout: 1000 }, () => {
  const node = fixtures[6].node as Extract<TreeNode, { kind: 'mqTopic' }>;
  const first = workbenchTargetFromNode(node)!;
  const second = workbenchTargetFromNode({ ...node, database: 'FactoryDB:West' })!;
  assert.notEqual(first.database, second.database);
  assert.equal(first.resource!.topic, name);
  assert.equal(first.resource!.scope, 'database');
  assert.equal(first.resource!.persistenceScope, 'instance');
  assert.deepEqual(first.resource!.persistence, {
    scope: 'instance', path: '.system/mq', shared: true, includedInDatabaseBackup: false,
  });
  assert.notEqual(buildWorkbenchUrl(profile.baseUrl, first), buildWorkbenchUrl(profile.baseUrl, second));
});

test('Graph resource stays Beta while other resources retain their database boundary', { timeout: 1000 }, () => {
  const graph = workbenchTargetFromNode(fixtures[8].node)!.resource!;
  assert.equal(graph.stability, 'beta');
  assert.equal(graph.beta, true);
  const measurement = workbenchTargetFromNode(fixtures[0].node)!.resource!;
  assert.equal(measurement.beta, false);
  assert.equal(measurement.persistenceScope, 'database');
  assert.equal(measurement.persistence.includedInDatabaseBackup, true);
});

test('Workbench URLs round-trip original database and resource names with only navigation fields', { timeout: 1000 }, () => {
  const originalName = 'DeviceID: #/"中文"?&=+%';
  const target = workbenchTargetFromNode({
    ...base, kind: 'measurement', measurement: { name: originalName, columns: [] },
  })!;
  const url = new URL(buildWorkbenchUrl(`${profile.baseUrl}/?token=must-not-forward#secret`, target));
  assert.equal(url.pathname, '/admin/app/sql');
  assert.equal(url.hash, '');
  assert.equal(url.searchParams.get('database'), database);
  assert.equal(url.searchParams.get('node'), originalName);
  assert.equal(url.searchParams.get('model'), 'measurement');
  assert.equal(url.searchParams.get('tool'), 'measurement');
  assert.deepEqual([...url.searchParams.keys()].sort(), ['database', 'model', 'node', 'tool']);
  assert.equal(url.toString().includes('must-not-forward'), false);
  assert.equal(url.toString().includes(profile.tokenSecretKey!), false);
});

test('index deep links use their legacy owner key and keep lifecycle routes without a tool', { timeout: 1000 }, () => {
  for (const index of [4, 5, 9, 10]) {
    const fixture = fixtures[index];
    const url = new URL(buildWorkbenchUrl(profile.baseUrl, workbenchTargetFromNode(fixture.node)!));
    assert.equal(url.searchParams.get('node'), fixture.key);
    assert.equal(url.searchParams.get('tool'), index >= 9 ? null : fixture.model);
  }
});

test('database-only targets preserve selected node context instead of the profile default', { timeout: 1000 }, () => {
  const target = workbenchTargetFromNode({ kind: 'database', profile, name: database, active: false })!;
  const url = new URL(buildWorkbenchUrl('https://sonnet.example/proxy/', target));
  assert.equal(url.pathname, '/proxy/admin/app/sql');
  assert.deepEqual([...url.searchParams.entries()], [['database', database]]);
  assert.equal(workbenchTargetFromNode(undefined), undefined);
  assert.equal(workbenchTargetFromNode({ kind: 'column', name, dataType: 'STRING', role: 'TAG' }), undefined);
  assert.equal(workbenchTargetFromNode({ kind: 'error', label: 'Unavailable' }), undefined);
});

test('Workbench URLs reject embedded credentials and non-HTTP destinations', { timeout: 1000 }, () => {
  for (const baseUrl of ['https://user:secret@sonnet.example', 'file:///tmp/db', 'javascript:alert(1)']) {
    assert.throws(() => buildWorkbenchUrl(baseUrl, { database }), /HTTP\(S\).*without embedded credentials/u);
  }
});

test('Workbench URLs reject cross-database resources and empty identities', { timeout: 1000 }, () => {
  const target = workbenchTargetFromNode(fixtures[0].node)!;
  assert.throws(() => buildWorkbenchUrl(profile.baseUrl, { ...target, database: 'OtherDB' }), /different database/u);
  assert.throws(() => buildWorkbenchUrl(profile.baseUrl, { database: '' }), /must not be empty/u);
  assert.throws(() => workbenchTargetFromNode({ ...base, kind: 'measurement', measurement: { name: '', columns: [] } }), /must not be empty/u);
});
