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
const targetPages = ['ai-connect', 'copilot-settings', 'rag', 'tool-permissions'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  'ai-connect': ['客户端', '传输方式', '数据库', '配置范围', 'HTTP Endpoint', '结果预算', '工具清单状态', '外发策略'],
  'copilot-settings': ['Provider', 'Chat 模型', 'Embedding profile', '账号状态', 'Token 到期', 'usage 时间窗', '实际/估算标记', '外发范围', '质量/成本证据'],
  rag: ['数据库', 'Stream', 'Active revision', 'Profile identity', 'Generation', 'Expected revision', '内容 / 分块', '任务 ID', '发布状态', '清理范围'],
  'tool-permissions': ['客户端', '用户身份', '数据库', '工具类别', '有效权限交集', 'maxRows', '字节预算', '超时/取消', '数据外发范围', '调用记录来源'],
};

const pages = catalog.sections.flatMap((section) => section.pages);

test('WB-02F adds six-state contracts to the four AI and MCP pages', () => {
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

test('WB-02F injects page contracts into every AI and MCP task', () => {
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

test('WB-02F preserves AI, RAG, permission, and egress boundaries', () => {
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.ok(byId.get('ai-connect').capabilities.some((capability) => capability.id === 'ai-connect-streamable-http' && capability.status === 'existing'));
  assert.ok(byId.get('ai-connect').capabilities.some((capability) => capability.id === 'ai-connect-stdio-bridge' && capability.status === 'planned'));
  assert.ok(byId.get('copilot-settings').capabilities.some((capability) => capability.id === 'copilot-quality-cost-gate' && capability.status === 'planned'));
  assert.ok(byId.get('rag').capabilities.some((capability) => capability.id === 'rag-persistent-task' && capability.status === 'existing'));
  assert.ok(byId.get('tool-permissions').capabilities.some((capability) => capability.id === 'tool-permissions-intersection'));
  assert.ok(taskDetails['ai-connect'].some((entry) => /tools\/list|stdio|不发起/.test(entry.description)));
  assert.ok(taskDetails.rag.some((entry) => /generation|profile|revision/.test(entry.description)));
  assert.ok(taskDetails['tool-permissions'].some((entry) => /外发|只读|权限/.test(entry.description)));
});
