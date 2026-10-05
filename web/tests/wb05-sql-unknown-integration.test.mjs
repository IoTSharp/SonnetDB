import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';

const sourcePath = new URL('../src/composables/useSqlExecution.ts', import.meta.url);
const source = readFileSync(sourcePath, 'utf8');

function synthetic(exports) {
  return new SyntheticModule(Object.keys(exports), function () {
    for (const [name, value] of Object.entries(exports)) this.setExport(name, value);
  });
}

async function loadExecutionContract() {
  const transformed = stripTypeScriptTypes(source, { mode: 'transform' });
  const modules = new Map();
  const dependencies = {
    vue: synthetic({ computed() {}, onScopeDispose() {}, ref() {}, watch() {} }),
    '@/api/sql': synthetic({ execControlPlaneSql() {}, execDataSql() {}, execDataSqlBatch() {} }),
    '@/api/sqlSplit': synthetic({ splitSqlStatements() {} }),
    '@/api/sqlMeta': synthetic({ buildClientErrorResultSet() {}, buildClientResultSet() {}, parseSqlMetaCommand() {} }),
    '@/api/sqlFormat': synthetic({ formatSqlDocument() {} }),
    '@/stores/sqlConsole': synthetic({ CONTROL_PLANE_KEY: '__control_plane__', DEFAULT_RESULT_PREVIEW_MAX_ROWS: 10_000 }),
    '@/utils/writeApproval': synthetic({ canConfirmWriteApproval() {}, markWriteApprovalStale() {} }),
    '@/utils/sqlWorkbench': synthetic({
      buildCreateDraft() {}, buildPreviewPlan() {}, buildSelectDraft() {}, defaultSqlForDb() {},
      formatSqlIdentifier() {}, isDatabaseCatalogMutating() {}, isSchemaMutating() {}, makeStatementId() {},
      normalizeSql() {}, quickSqlOptions() {}, statementTitle(value) { return value; }, summarizeSqlResult() {},
    }),
  };
  const module = new SourceTextModule(transformed, { identifier: sourcePath.href });
  await module.link((specifier) => {
    const dependency = dependencies[specifier];
    assert.ok(dependency, `Unexpected dependency: ${specifier}`);
    return dependency;
  });
  await module.evaluate({ timeout: 3000 });
  return module.namespace;
}

test('SQL cancellation, transport interruption and missing terminal marker stay unknown', { timeout: 5000 }, async () => {
  const { classifySqlHistoryResult } = await loadExecutionContract();
  for (const code of ['sql_execution_cancelled', 'sql_transport_error', 'incomplete_sql_response', 'invalid_sql_response']) {
    assert.deepEqual(
      classifySqlHistoryResult({ error: { code, message: 'uncertain' }, end: null }),
      { status: 'unknown', completeness: 'unknown' },
    );
  }
  assert.deepEqual(
    classifySqlHistoryResult({ error: null, end: null }),
    { status: 'unknown', completeness: 'unknown' },
  );
});

test('server errors remain errors while completed and truncated results retain terminal completeness', { timeout: 5000 }, async () => {
  const { classifySqlHistoryResult } = await loadExecutionContract();
  assert.deepEqual(
    classifySqlHistoryResult({ error: { code: 'forbidden', message: 'denied' }, end: null }),
    { status: 'error' },
  );
  assert.deepEqual(
    classifySqlHistoryResult({ error: null, end: { type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 1 } }),
    { status: 'success', completeness: 'complete' },
  );
  assert.deepEqual(
    classifySqlHistoryResult({ error: null, end: { type: 'end', rowCount: 1, recordsAffected: -1, elapsedMs: 1, truncated: true } }),
    { status: 'success', completeness: 'truncated' },
  );
});

test('recordStatementHistory stores the classified status and completeness', () => {
  assert.match(source, /const outcome = classifySqlHistoryResult\(result\)/u);
  assert.match(source, /status: outcome\.status/u);
  assert.match(source, /completeness: outcome\.completeness/u);
  assert.match(source, /不能记录为成功/u);
  assert.match(source, /canConfirmWriteApproval\(plan, currentApprovalContext\)/u);
  assert.match(source, /markWriteApprovalStale\(plan/u);
  assert.match(source, /draftFingerprint: normalizeSql\(sql\.value\)/u);
});
