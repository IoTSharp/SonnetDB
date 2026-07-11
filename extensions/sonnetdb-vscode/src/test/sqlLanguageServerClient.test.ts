import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { SqlLanguageServerClient } from '../core/sqlLanguageServerClient';

test('SqlLanguageServerClient exchanges validation requests over JSON lines', async () => {
  const fixture = [
    "process.stdin.setEncoding('utf8');",
    "let pending = '';",
    "process.stdin.on('data', chunk => {",
    "  pending += chunk;",
    "  const lines = pending.split(/\\r?\\n/);",
    "  pending = lines.pop() || '';",
    "  for (const line of lines) {",
    "    const request = JSON.parse(line);",
    "    const diagnostics = request.text.includes('@')",
    "      ? [{ severity: 'error', message: 'bad token', offset: request.text.indexOf('@'), length: 1 }]",
    "      : [];",
    "    process.stdout.write(JSON.stringify({ id: request.id, diagnostics }) + '\\n');",
    "  }",
    "});",
  ].join('\n');
  const client = new SqlLanguageServerClient({ command: process.execPath, args: ['-e', fixture] });

  try {
    assert.deepEqual(await client.validate('SELECT 1'), []);
    assert.deepEqual(await client.validate('SELECT @'), [{
      severity: 'error',
      message: 'bad token',
      offset: 7,
      length: 1,
    }]);
  } finally {
    client.dispose();
  }
});
