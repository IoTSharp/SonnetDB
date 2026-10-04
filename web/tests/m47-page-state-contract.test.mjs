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
const targetModels = ['measurement', 'table', 'document', 'vector', 'fulltext'];
const expectedStates = ['normal', 'empty', 'error', 'permission', 'readonly', 'longContent'];
const allowedStatuses = new Set(['existing', 'extension', 'planned']);
const requiredNormalFields = {
  measurement: ['时区', '选中 Series'],
  table: ['物化预算', '草稿差异'],
  document: ['Sort', '文档 ID', 'Payload 呈现模式'],
  vector: ['显式 Profile 身份'],
  fulltext: ['Analyzer 身份', '重建任务 ID'],
};

test('WB-02B adds six-state contracts to the next five model pages', () => {
  assert.equal(catalog.models.length, 9);
  for (const modelId of targetModels) {
    const page = catalog.models.find((candidate) => candidate.id === modelId);
    assert.ok(page, `missing model ${modelId}`);
    assert.ok(Array.isArray(page.capabilities) && page.capabilities.length > 0, `${modelId} capabilities`);
    assert.deepEqual(Object.keys(page.stateMatrix).sort(), [...expectedStates].sort(), `${modelId} states`);
    for (const field of requiredNormalFields[modelId]) {
      assert.ok(page.stateMatrix.normal.fields.includes(field), `${modelId} normal field ${field}`);
    }
    for (const capability of page.capabilities) {
      assert.match(capability.id, new RegExp(`^${modelId}-`));
      assert.ok(allowedStatuses.has(capability.status), `${modelId} capability status`);
      assert.ok(capability.label && capability.note, `${modelId} capability explanation`);
    }
    for (const stateId of expectedStates) {
      const state = page.stateMatrix[stateId];
      assert.ok(state.label && state.summary && state.primary, `${modelId}.${stateId} summary`);
      assert.ok(allowedStatuses.has(state.status), `${modelId}.${stateId} status`);
      assert.ok(Array.isArray(state.fields) || Array.isArray(state.preserve) || Array.isArray(state.blocked) || Array.isArray(state.limits), `${modelId}.${stateId} details`);
    }
  }
});

test('WB-02B injects the same page contract into every model task', () => {
  for (const modelId of targetModels) {
    const page = catalog.models.find((candidate) => candidate.id === modelId);
    const entries = taskDetails[modelId];
    assert.ok(Array.isArray(entries) && entries.length > 0, `${modelId} task details`);
    for (const entry of entries) {
      assert.strictEqual(entry.stateMatrix, page.stateMatrix, `${modelId} state matrix identity`);
      assert.strictEqual(entry.capabilities, page.capabilities, `${modelId} capability identity`);
    }
  }
});
