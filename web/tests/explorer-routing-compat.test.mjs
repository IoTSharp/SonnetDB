import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = readFileSync(new URL('../src/composables/useSqlExplorerRouting.ts', import.meta.url), 'utf8');

function functionBody(name, nextName) {
  const start = source.indexOf(`function ${name}`);
  assert.notEqual(start, -1, `routing composable should keep ${name}`);
  const end = nextName === undefined ? source.length : source.indexOf(`function ${nextName}`, start);
  assert.ok(end > start, `${name} should have a bounded body`);
  return source.slice(start, end);
}

function queryBranch(body, model, tool = model) {
  return new RegExp(
    `item\\.model === '${model}'[\\s\\S]{0,140}\\{\\s*tool:\\s*'${tool}',\\s*model:\\s*item\\.model,\\s*node:\\s*item\\.name\\s*\\}`,
  );
}

test('Explorer model routes preserve SQL deep-link shape and database selection', () => {
  const body = functionBody('routeExplorerItem', 'openExplorerItem');
  assert.match(body, /selectExplorerItem\(db, item\)/);
  assert.match(body, /name:\s*'sql'/);
  for (const model of ['measurement', 'table', 'document', 'kv', 'mq', 'vector', 'fulltext', 'bucket', 'graph']) {
    assert.match(body, queryBranch(body, model));
  }
});

test('index and backup keep the legacy model/node route fallback', () => {
  const body = functionBody('routeExplorerItem', 'openExplorerItem');
  assert.match(body, /:\s*\{\s*model:\s*item\.model,\s*node:\s*item\.name\s*\}/);
  assert.doesNotMatch(body, /tool:\s*'(?:index|backup)'/);
});

test('Open in SQL keeps the current model-specific action boundary', () => {
  const body = functionBody('openExplorerItemInSql', 'openMeasurement');
  for (const action of ['openMeasurement', 'openTable', 'openDocumentCollection', 'showIndex']) {
    assert.match(body, new RegExp(`${action}\\(`));
  }
  assert.match(body, /item\.model === 'backup'[\s\S]{0,100}runHealthCheck\(\)/);
  assert.match(body, /item\.model === 'vector'[\s\S]{0,180}setWorkbenchTool\('sql'\)[\s\S]{0,180}setSqlDraft\(/);
  assert.match(body, /item\.model === 'fulltext'[\s\S]{0,180}setWorkbenchTool\('sql'\)[\s\S]{0,180}setSqlDraft\(/);
  assert.doesNotMatch(body, /item\.model === 'kv'/);
  assert.doesNotMatch(body, /item\.model === 'mq'/);
  assert.doesNotMatch(body, /item\.model === 'bucket'/);
});

test('canOpenInSql retains the current allowlist and excludes KV, MQ, and Bucket', () => {
  const body = functionBody('canOpenInSql', 'selectExplorerItem');
  for (const model of ['measurement', 'table', 'document', 'index', 'vector', 'fulltext', 'graph', 'backup']) {
    assert.match(body, new RegExp(`item\\.model === '${model}'`));
  }
  for (const model of ['kv', 'mq', 'bucket']) {
    assert.doesNotMatch(body, new RegExp(`item\\.model === '${model}'`));
  }
});

test('SQL deep links only replace route state and do not auto-execute', () => {
  const body = functionBody('routeExplorerItem', 'openExplorerItem');
  assert.match(body, /void router\.replace\(/);
  assert.doesNotMatch(body, /setSqlDraft\(|runHealthCheck\(|openExplorerItemInSql\(|execute|executeQuery|runQuery/);
});
