import assert from 'node:assert/strict';
import test from 'node:test';
import { scanSqlDiagnostics } from '../core/sqlDiagnostics';

test('scanSqlDiagnostics ignores balanced syntax inside comments and strings', () => {
  assert.deepEqual(scanSqlDiagnostics("SELECT '(value)' /* ) */ FROM cpu;"), []);
});

test('scanSqlDiagnostics reports unclosed quotes and parentheses', () => {
  const issues = scanSqlDiagnostics("SELECT ('value");
  assert.deepEqual(issues.map((issue) => issue.message), [
    "Unclosed ' quote.",
    'Unmatched opening parenthesis.',
  ]);
});
