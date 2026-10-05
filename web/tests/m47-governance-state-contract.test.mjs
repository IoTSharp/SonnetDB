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
const targetPages = ['users', 'grants', 'tokens', 'approvals', 'backup'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  users: ['实例身份', '用户名', '超级用户', '创建时间', 'Token 数', '关联授权数', '凭据状态', '最后变更时间'],
  grants: ['实例身份', '用户', '数据库', '权限', 'MQ Topic 范围', '来源', '有效权限', '撤销影响'],
  tokens: ['实例身份', '所属用户', 'Token 标识（脱敏）', '创建时间', '有效期（服务端）', '状态', '最后使用时间', '撤销影响'],
  approvals: ['连接', '范围（数据库/实例）', '模型', '对象', '动作', '影响数量', '风险', '发起人', '有效期', '请求 ID', '服务器终态', '审计来源'],
  backup: ['实例身份', '数据库', '源目录/文件', '版本', 'manifest', 'checksum', '备份范围', '验证状态', '目标数据库', '目标目录', '覆盖确认', '恢复影响', 'MQ Store 范围'],
};

const pages = catalog.sections.flatMap((section) => section.pages);

test('WB-02G adds six-state contracts to the five governance pages', () => {
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

test('WB-02G injects page contracts into every governance task', () => {
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

test('WB-02G preserves permission, secret, approval, and backup boundaries', () => {
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.ok(byId.get('users').capabilities.some((capability) => capability.id === 'users-control-plane-list'));
  assert.ok(byId.get('grants').capabilities.some((capability) => capability.id === 'grants-mq-database-scope'));
  assert.ok(byId.get('tokens').capabilities.some((capability) => capability.id === 'tokens-create-once-display'));
  assert.ok(byId.get('approvals').capabilities.some((capability) => capability.id === 'approvals-no-write-auto-retry'));
  assert.ok(byId.get('backup').capabilities.some((capability) => capability.id === 'backup-mq-shared-store-boundary'));
  assert.ok(taskDetails.tokens.some((entry) => /一次性|明文|撤销/.test(entry.description)));
  assert.ok(taskDetails.approvals.some((entry) => /服务器执行终态|不自动重试|本地草稿/.test(entry.description)));
  assert.ok(taskDetails.backup.some((entry) => /\.system\/mq|单库备份|跨数据库/.test(entry.description)));
});
