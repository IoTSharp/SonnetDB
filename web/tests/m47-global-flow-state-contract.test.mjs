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
const targetPages = ['summary', 'recent', 'imports', 'transfers', 'jobs'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  summary: ['连接', '数据库数', '活动工作区', '待审批', '需关注', '健康检查时间', '数据来源'],
  recent: ['宿主', '连接', '数据库', '工作区', '当前页签', '输入 / 过滤器', '保存时间', '草稿状态'],
  imports: ['数据库', '模型', '目标对象', '源文件', '格式', '行数 / 字节', '字段映射', '重复策略', '失败策略', '任务状态'],
  transfers: ['数据库', 'Bucket', 'Key', '方向', '版本', '字节', '已完成分片', '有效期', 'checksum', '取消状态'],
  jobs: ['范围', '数据库', '模型', '任务 ID', 'generation', 'revision', 'offset', '对象版本', '发布点', '状态', '可恢复原因'],
};

const pages = catalog.sections.flatMap((section) => section.pages);

test('WB-02E adds six-state contracts to the five global flow pages', () => {
  assert.equal(catalog.sections.length, 7);
  assert.equal(catalog.models.length, 9);
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

test('WB-02E injects page contracts into every selected task', () => {
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

test('WB-02E preserves restore, transfer, and heterogeneous resume boundaries', () => {
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.equal(byId.get('summary').scope, 'instance');
  assert.ok(byId.get('summary').capabilities.some((capability) => /MQ 持久化边界/.test(capability.label)));
  assert.ok(byId.get('recent').capabilities.some((capability) => capability.id === 'recent-context-restore'));
  assert.ok(byId.get('imports').capabilities.some((capability) => capability.id === 'import-persistent-resume' && capability.status === 'planned'));
  assert.ok(byId.get('transfers').capabilities.some((capability) => capability.id === 'transfer-cancel'));
  assert.ok(byId.get('jobs').capabilities.some((capability) => capability.id === 'jobs-mq-offset'));
  assert.ok(taskDetails.recent.some((entry) => /不会自动执行|不重放/.test(entry.description)));
  assert.ok(taskDetails.jobs.some((entry) => /generation|revision|offset|object version/.test(entry.description)));
});
