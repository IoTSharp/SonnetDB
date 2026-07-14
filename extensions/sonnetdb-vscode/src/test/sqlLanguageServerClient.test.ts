import * as assert from 'node:assert/strict';
import { test } from 'node:test';
import { SqlLanguageServerClient } from '../core/sqlLanguageServerClient';

test('SqlLanguageServerClient exchanges validation requests over JSON-RPC LSP frames', async () => {
  const fixture = [
    "let pending = Buffer.alloc(0);",
    "process.stdin.on('data', chunk => {",
    "  pending = Buffer.concat([pending, chunk]);",
    "  while (true) {",
    "    const headerEnd = pending.indexOf('\\r\\n\\r\\n');",
    "    if (headerEnd < 0) return;",
    "    const header = pending.subarray(0, headerEnd).toString('ascii');",
    "    const length = Number(/Content-Length:\\s*(\\d+)/i.exec(header)[1]);",
    "    const bodyStart = headerEnd + 4;",
    "    if (pending.length < bodyStart + length) return;",
    "    const request = JSON.parse(pending.subarray(bodyStart, bodyStart + length).toString('utf8'));",
    "    pending = pending.subarray(bodyStart + length);",
    "    const diagnostics = request.params.text.includes('@')",
    "      ? [{ severity: 'error', message: 'bad token', offset: request.params.text.indexOf('@'), length: 1 }]",
    "      : [];",
    "    const payload = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: { diagnostics } }));",
    "    process.stdout.write(Buffer.concat([Buffer.from('Content-Length: ' + payload.length + '\\r\\n\\r\\n'), payload]));",
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
