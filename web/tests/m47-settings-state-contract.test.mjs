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
const targetPages = ['preferences', 'server-settings', 'studio-host', 'capability-matrix', 'about'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const requiredNormalFields = {
  preferences: ['宿主', '主题', '密度', '编辑器字号', '默认结果上限', 'Explorer 宽度', 'Inspector 宽度', '保存范围'],
  'server-settings': ['实例', '配置来源', 'Prometheus 来源', 'SQL / MCP / Object 预算', '敏感字段摘要', '是否需重启', '管理员权限', 'MQ 持久化范围'],
  'studio-host': ['宿主', 'Native bridge', 'Managed Local', 'Data root', '凭据存储', 'Server 版本', 'PID / 启动时间', '进程归属'],
  'capability-matrix': ['宿主', 'manifest', 'Server capabilities', 'API contract', 'MCP contract', 'uiContractVersion', '发行版本', '校验 / 签名', 'Web/Studio/VS Code 证据', 'Graph 稳定性'],
  about: ['产品', '当前宿主', '发行物', '真实版本来源', '九模型语义', '快捷键', '许可证', '诊断摘要范围'],
};

const pages = catalog.sections.flatMap((section) => section.pages);

test('WB-02H adds six-state contracts to the five settings and release pages', () => {
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

test('WB-02H injects page contracts into every settings task', () => {
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

test('WB-02H preserves host, release, and diagnostic evidence boundaries', () => {
  const byId = new Map(pages.map((page) => [page.id, page]));
  assert.ok(byId.get('preferences').capabilities.some((capability) => capability.id === 'preferences-result-budget-boundary'));
  assert.ok(byId.get('server-settings').capabilities.some((capability) => capability.id === 'server-settings-sensitive-redaction'));
  assert.ok(byId.get('studio-host').capabilities.some((capability) => capability.id === 'studio-host-lifecycle-boundary'));
  assert.ok(byId.get('capability-matrix').capabilities.some((capability) => capability.id === 'capability-matrix-signature-evidence' && capability.status === 'planned'));
  assert.ok(byId.get('about').capabilities.some((capability) => capability.id === 'about-release-version'));
  assert.ok(taskDetails['studio-host'].some((entry) => /Start\/Stop|安装|Extension Host|宿主/.test(entry.description)));
  assert.ok(taskDetails['capability-matrix'].some((entry) => /manifest|签名|未就绪|PASS/.test(entry.description)));
  assert.ok(taskDetails.about.some((entry) => /版本|发行物|快捷键/.test(entry.description)));
});
