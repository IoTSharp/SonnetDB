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
const targetPages = ['events', 'slow-queries', 'alerts', 'runtime', 'modbus'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  events: ['级别', '来源', '数据库/实例', '事件时间', '接收时间', '正文/Payload', 'SSE 状态'],
  'slow-queries': ['数据库', '最短耗时', '时间范围', '事件时间', '耗时', '行数', '状态', 'SQL 摘要', '阈值来源'],
  alerts: ['规则名称', '指标', '评估窗口', '阈值', '去抖窗口', '通知路由', '作用范围', '草稿状态'],
  runtime: ['组件', '范围', '可见信息', '时间预算', '字节预算', '脱敏策略', '取消能力', '证据状态'],
  modbus: ['数据库', '源/端点', '模式', '地址', '寄存器范围', '数据库/表绑定', 'Runtime 状态', '请求 ID', '审批状态'],
};

test('WB-02D adds six-state contracts to observe and flow pages', () => {
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

test('WB-02D injects the page contract into every selected task', () => {
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

test('WB-02D preserves observation and field-write boundaries', () => {
  const pages = catalog.sections.flatMap((section) => section.pages);
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.equal(byId.get('alerts').capabilities.every((capability) => capability.status === 'planned'), true);
  assert.equal(byId.get('runtime').capabilities.filter((capability) => capability.id !== 'runtime-native-handoff').every((capability) => capability.status === 'planned'), true);
  assert.equal(byId.get('events').capabilities.some((capability) => capability.id === 'events-view-pause'), true);
  assert.equal(byId.get('slow-queries').capabilities.some((capability) => capability.id === 'slow-query-input-restore'), true);
  assert.equal(byId.get('modbus').capabilities.some((capability) => capability.id === 'modbus-write-approval'), true);
  assert.ok(taskDetails['slow-queries'].some((entry) => /不自动重跑/.test(entry.description)));
});
