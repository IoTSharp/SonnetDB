import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { resolve } from 'node:path';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const sourceRoot = fileURLToPath(new URL('../src/', import.meta.url));

function synthetic(exports) {
  return new SyntheticModule(Object.keys(exports), function () {
    for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
  });
}

async function loadHistoryContract() {
  const path = resolve(sourceRoot, 'stores/workbenchHistory.ts');
  const source = stripTypeScriptTypes(readFileSync(path, 'utf8'), { mode: 'transform' });
  const vue = synthetic({
    computed: (factory) => ({ get value() { return factory(); } }),
    ref: (value) => ({ value }),
    watch: () => () => {},
  });
  const pinia = synthetic({ defineStore: (_id, setup) => setup });
  const module = new SourceTextModule(source, { identifier: path });
  await module.link((name) => {
    if (name === 'vue') return vue;
    if (name === 'pinia') return pinia;
    throw new Error(`Unexpected dependency: ${name}`);
  });
  await module.evaluate({ timeout: 3000 });
  return module.namespace;
}

test('history preserves unknown terminal state and redacts credentials', { timeout: 5000 }, async () => {
  const history = await loadHistoryContract();
  assert.equal(history.normalizeWorkbenchHistoryStatus('server-pending'), 'unknown');
  assert.equal(history.normalizeWorkbenchHistoryStatus('success'), 'success');
  assert.match(
    history.redactWorkbenchText("ISSUE TOKEN: 'secret-value' Authorization Bearer abc123"),
    /\[redacted\]/u,
  );
  assert.doesNotMatch(
    history.redactWorkbenchText("ISSUE TOKEN: 'secret-value' Authorization Bearer abc123"),
    /secret-value|abc123/u,
  );
});

test('history redaction covers quoted JSON and JSONL sensitive keys', { timeout: 5000 }, async () => {
  const history = await loadHistoryContract();
  const input = [
    '{"token":"token-value","authorization":"Bearer auth-value","access_token":"access-value","password":"password-value","secret":"secret-value","apiKey":"api-value","message":"token: ordinary"}',
    '{"id":"keep","value":"ordinary"}',
  ].join('\\n');
  const redacted = history.redactWorkbenchText(input);

  assert.doesNotMatch(
    redacted,
    /token-value|auth-value|access-value|password-value|secret-value|api-value/u,
  );
  assert.match(redacted, /"token":"\[redacted\]"/u);
  assert.match(redacted, /"authorization":"\[redacted\]"/u);
  assert.match(redacted, /"access_token":"\[redacted\]"/u);
  assert.match(redacted, /"password":"\[redacted\]"/u);
  assert.match(redacted, /"secret":"\[redacted\]"/u);
  assert.match(redacted, /"apiKey":"\[redacted\]"/u);
  assert.match(redacted, /"message":"token: ordinary"/u);
  assert.match(redacted, /"id":"keep","value":"ordinary"/u);
});

test('result and draft contracts expose bounded previews, unknown results and closed-draft recovery', () => {
  const resultPanel = readFileSync(resolve(sourceRoot, 'components/WorkbenchResultPanel.vue'), 'utf8');
  const sqlConsole = readFileSync(resolve(sourceRoot, 'stores/sqlConsole.ts'), 'utf8');
  const historyDrawer = readFileSync(resolve(sourceRoot, 'components/WorkbenchHistoryDrawer.vue'), 'utf8');
  const sqlWorkspace = readFileSync(resolve(sourceRoot, 'components/SqlQueryWorkspace.vue'), 'utf8');
  const sqlConsoleView = readFileSync(resolve(sourceRoot, 'views/SqlConsoleView.vue'), 'utf8');

  assert.match(resultPanel, /isTruncated/);
  assert.match(resultPanel, /结果已截断/u);
  assert.match(resultPanel, /不会将此操作显示为成功/u);
  assert.match(resultPanel, /loaded preview only/u);
  assert.match(sqlConsole, /DEFAULT_RESULT_PREVIEW_MAX_ROWS/);
  assert.match(sqlConsole, /closedTabs/);
  assert.match(sqlConsole, /function reopenTab/);
  assert.match(sqlConsole, /不会自动执行 SQL/u);
  assert.match(historyDrawer, /待核对/u);
  assert.match(historyDrawer, /不会自动重放写操作/u);
  assert.match(sqlWorkspace, /恢复草稿/u);
  assert.match(sqlWorkspace, /closedTabs/);
  assert.match(sqlConsoleView, /:closed-tabs="sqlConsole\.closedTabs"/);
  assert.match(sqlConsoleView, /@reopen-closed-tab="reopenClosedTab"/);
  assert.match(sqlConsoleView, /@discard-closed-tab="discardClosedTab"/);
});
