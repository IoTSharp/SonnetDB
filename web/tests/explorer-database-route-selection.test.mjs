import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const routingSource = readFileSync(new URL('../src/composables/useSqlExplorerRouting.ts', import.meta.url), 'utf8');
const viewSource = readFileSync(new URL('../src/views/SqlConsoleView.vue', import.meta.url), 'utf8');

function functionBody(source, name, nextName) {
  const start = source.indexOf(`function ${name}`);
  assert.notEqual(start, -1, `${name} should remain available`);
  const end = nextName === undefined ? source.length : source.indexOf(`function ${nextName}`, start);
  assert.ok(end > start, `${name} should have a bounded body`);
  return source.slice(start, end);
}

test('new Explorer links add database context while preserving legacy query fields', () => {
  const body = functionBody(routingSource, 'routeExplorerItem', 'openExplorerItem');
  assert.match(body, /const query = item\.model === 'table'/);
  for (const model of ['measurement', 'table', 'document', 'kv', 'mq', 'vector', 'fulltext', 'bucket', 'graph']) {
    assert.match(body, new RegExp(`item\\.model === '${model}'[\\s\\S]{0,180}\\{[\\s\\S]*?tool: '${model}', model: item\\.model, node: item\\.name`));
  }
  assert.match(body, /database:\s*item\.resource\.database \|\| db/);
  assert.match(body, /query:\s*\{[\s\S]{0,100}\.\.\.query/);
  assert.doesNotMatch(body, /setSqlDraft\(|runHealthCheck\(|openExplorerItemInSql\(/);
});

test('database-bearing route selection waits for the list and chooses only valid databases', () => {
  assert.match(viewSource, /routeDatabaseListReady = ref\(false\)/);
  assert.match(viewSource, /routeDatabaseSelectionToken = ref\(''\)/);
  assert.match(viewSource, /route\.query\.database/);
  assert.match(viewSource, /if \(!requested\)/);
  assert.match(viewSource, /if \(!routeDatabaseListReady\.value\) return 'pending'/);
  assert.match(viewSource, /if \(!databases\.value\.includes\(requested\)\) return 'invalid'/);
  assert.match(viewSource, /selectDatabase\(requested\)/);
  assert.match(viewSource, /if \(requestedDatabase\) \{[\s\S]{0,80}if \(!_databases\.includes\(requestedDatabase\)\)/);
  assert.match(viewSource, /routeSelectionToken\.value = `invalid-db/);
  assert.match(viewSource, /routeDatabaseSelectionToken\.value = `invalid-db/);
  assert.match(viewSource, /routeDatabaseSelectionToken\.value !== requestedDatabase/);
  assert.match(viewSource, /else if \(db !== requestedDatabase\)/);
  assert.match(viewSource, /explorerKeyFromRoute\(model, node, dbSchema, management\)/);
  assert.ok(
    viewSource.indexOf('const routeDatabaseListReady') < viewSource.indexOf('watch(\n  [')
      && viewSource.indexOf('routeDatabaseListReady.value = true') < viewSource.indexOf('applyRouteDatabaseSelection();'),
    'database selection guard must be established before route key projection',
  );
});

test('legacy links without database keep active/default database fallback and route-only behavior', () => {
  assert.match(viewSource, /if \(!requested\)/);
  assert.match(viewSource, /if \(!model\) \{/);
  assert.match(viewSource, /else if \(!_databases\.includes\(requestedDatabase\)\)/);
  assert.match(viewSource, /if \(db !== requestedDatabase\) selectDatabase\(requestedDatabase\)/);
  assert.match(viewSource, /if \(!db \|\| db === CONTROL_PLANE_KEY \|\| !dbSchema \|\| !management\) return/);
  assert.doesNotMatch(viewSource, /route\.query\.database[\s\S]{0,250}run\(/);
  assert.doesNotMatch(viewSource, /confirmPreview\(\)|executeQuery/);
});

test('an invalid database token can recover when the database becomes available', () => {
  assert.match(viewSource, /routeDatabaseSelectionToken\.value = `invalid-db\\u0000\$\{requestedDatabase\}`/);
  assert.match(viewSource, /routeDatabaseSelectionToken\.value !== requestedDatabase[\s\S]{0,180}selectDatabase\(requestedDatabase\)/);
});
