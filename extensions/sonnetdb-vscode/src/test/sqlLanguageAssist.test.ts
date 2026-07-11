import assert from 'node:assert/strict';
import test from 'node:test';
import { findSqlFunctionCall, repairSqlDiagnostic } from '../core/sqlLanguageAssist';

test('findSqlFunctionCall returns the nested function and active parameter', () => {
  const source = "SELECT forecast(moving_average(value, 5), 12, 0.95) FROM metrics";

  const movingAverage = findSqlFunctionCall(source, source.indexOf('5') + 1);
  assert.equal(movingAverage?.signature.name, 'moving_average');
  assert.equal(movingAverage?.activeParameter, 1);

  const forecast = findSqlFunctionCall(source, source.indexOf('0.95') + 2);
  assert.equal(forecast?.signature.name, 'forecast');
  assert.equal(forecast?.activeParameter, 2);
});

test('findSqlFunctionCall ignores commas in strings and comments', () => {
  const source = "SELECT fill(value, 'linear,strict' /* , ignored */)";
  const call = findSqlFunctionCall(source, source.indexOf('strict'));

  assert.equal(call?.signature.name, 'fill');
  assert.equal(call?.activeParameter, 1);
});

test('repairSqlDiagnostic creates deterministic delimiter fixes', () => {
  assert.deepEqual(
    repairSqlDiagnostic('Unmatched opening parenthesis.', 7, 1, 20),
    { title: 'Insert closing parenthesis', offset: 20, length: 0, text: ')' },
  );
  assert.deepEqual(
    repairSqlDiagnostic('Unmatched closing parenthesis.', 7, 1, 20),
    { title: 'Remove unmatched parenthesis', offset: 7, length: 1, text: '' },
  );
  assert.deepEqual(
    repairSqlDiagnostic("Unclosed ' quote.", 7, 1, 20),
    { title: "Insert closing ' quote", offset: 20, length: 0, text: "'" },
  );
});
