import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import test from 'node:test';

const repositoryRoot = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const prototypeRoot = resolve(repositoryRoot, 'docs/design/m47-unified-management-workbench/prototype');
const context = { window: {} };

for (const fileName of ['catalog.js', 'task-details.js']) {
  const filePath = resolve(prototypeRoot, fileName);
  vm.runInNewContext(readFileSync(filePath, 'utf8'), context, { filename: filePath, timeout: 3000 });
}

const catalog = context.window.M47_CATALOG;
const taskDetails = context.window.M47_TASK_DETAILS;
const targetPages = ['database-catalog', 'connections', 'notebook', 'history', 'metrics'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  'database-catalog': ['名称', '权限', '资源', 'Segment'],
  connections: ['名称', 'URL', '默认数据库', '认证来源', '宿主', '测试时间'],
  notebook: ['单元类型', '输入', '执行状态', '结果快照', '保存版本'],
  history: ['时间', '对象', '动作', '状态', '耗时', '返回/影响数量'],
  metrics: ['写入速率', 'query P95', 'WAL fsync P95', '内存', '采样时间', '指标能力'],
};

test('WB-02C adds six-state contracts to five global pages', () => {
  assert.equal(catalog.sections.length, 7);
  assert.equal(catalog.models.length, 9);
  const pages = catalog.sections.flatMap((section) => section.pages);
  assert.equal(pages.length, 30);
  for (const pageId of targetPages) {
    const page = pages.find((candidate) => candidate.id === pageId);
    assert.ok(page, `missing page ${pageId}`);
    assert.ok(Array.isArray(page.capabilities) && page.capabilities.length > 0, `${pageId} capabilities`);
    assert.deepEqual(Object.keys(page.stateMatrix).sort(), [...expectedStates].sort(), `${pageId} states`);
    for (const field of requiredNormalFields[pageId]) {
      assert.ok(page.stateMatrix.normal.fields.includes(field), `${pageId} normal field ${field}`);
    }
  }
});

test('WB-02C keeps global task details aligned with page contracts', () => {
  const pages = catalog.sections.flatMap((section) => section.pages);
  for (const pageId of targetPages) {
    const page = pages.find((candidate) => candidate.id === pageId);
    const entries = taskDetails[pageId];
    assert.ok(Array.isArray(entries) && entries.length > 0, `${pageId} task details`);
    for (const entry of entries) {
      assert.strictEqual(entry.stateMatrix, page.stateMatrix, `${pageId} state matrix identity`);
      assert.strictEqual(entry.capabilities, page.capabilities, `${pageId} capability identity`);
    }
  }
});

test('WB-02C preserves the global page boundaries', () => {
  const pages = catalog.sections.flatMap((section) => section.pages);
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.equal(byId.get('notebook').capabilities.every((capability) => capability.status === 'planned'), true);
  assert.equal(byId.get('connections').capabilities.some((capability) => capability.id === 'connection-object-permissions' && capability.status === 'planned'), true);
  assert.equal(byId.get('history').capabilities.some((capability) => capability.id === 'history-input-restore'), true);
  assert.equal(byId.get('metrics').capabilities.some((capability) => capability.id === 'metrics-sample'), true);
  assert.match(byId.get('database-catalog').capabilities.find((capability) => capability.id === 'database-mq-context').note, /database \+ topic/);
  assert.match(byId.get('database-catalog').capabilities.find((capability) => capability.id === 'database-mq-context').note, /\.system\/mq/);
  const connectionTask = taskDetails.connections.find((entry) => entry.title === '连接测试');
  assert.ok(connectionTask.fields.some((field) => field.label === '数据库/对象权限'));
});
