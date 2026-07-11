import assert from 'node:assert/strict';
import test from 'node:test';
import { extractStatementAtOffset } from '../core/sqlText';

test('extractStatementAtOffset returns the statement around the cursor', () => {
  const source = 'SELECT 1;\nSELECT * FROM cpu WHERE host = \'pump;01\';\nSELECT 3;';
  assert.equal(
    extractStatementAtOffset(source, source.indexOf('FROM cpu')),
    "SELECT * FROM cpu WHERE host = 'pump;01';",
  );
});

test('extractStatementAtOffset ignores semicolons in comments', () => {
  const source = 'SELECT 1;\n-- ignore ; here\nSELECT 2 /* and ; here */;';
  assert.equal(
    extractStatementAtOffset(source, source.indexOf('SELECT 2')),
    '-- ignore ; here\nSELECT 2 /* and ; here */;',
  );
});
