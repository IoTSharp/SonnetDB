import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import { SourceTextModule, SyntheticModule } from 'node:vm';
import test from 'node:test';
import { AxiosHeaders } from 'axios';
import * as axios from 'axios';
import * as vue from 'vue';

const modules = new Map();
function external(name, exports) {
  const module = new SyntheticModule(Object.keys(exports), function () { for (const [key, value] of Object.entries(exports)) this.setExport(key, value); });
  modules.set(name, module); return module;
}
external('axios', axios); external('vue', vue);
modules.set('../api/sql', new SourceTextModule(stripTypeScriptTypes(readFileSync(new URL('../src/api/sql.ts', import.meta.url), 'utf8'), { mode: 'transform' })));
for (const name of ['policy', 'session', 'transport']) {
  modules.set(`./${name}`, new SourceTextModule(stripTypeScriptTypes(readFileSync(new URL(`../src/preview/${name}.ts`, import.meta.url), 'utf8'), { mode: 'transform' }), {
    initializeImportMeta(meta) { meta.env = { VITE_WORKBENCH_PROFILE: 'preview-1' }; },
  }));
}
for (const module of modules.values()) if (module.status === 'unlinked') await module.link((name) => {
  const dependency = modules.get(name); if (!dependency) throw new Error(`Unexpected import ${name}`); return dependency;
});
await modules.get('./transport').evaluate({ timeout: 1000 });
const policy = modules.get('./policy').namespace;
const session = modules.get('./session').namespace;
const { readPreviewBody, previewAdapter } = modules.get('./transport').namespace;
globalThis.localStorage = { removeItem() {} };
globalThis.window = { location: { origin: 'http://127.0.0.1:43210' } };

test('Preview routes reject all deferred modules, callback, arrays and unknown query keys', () => {
  for (const name of ['monitoring', 'events', 'modbus', 'rag', 'users', 'grants', 'tokens', 'ai-settings', 'copilot-test', 'auto-login', 'copilot-oauth-callback']) assert.equal(policy.previewRouteAllowed(name, {}), false, name);
  for (const tool of policy.previewTools) assert.equal(policy.previewRouteAllowed('sql', { tool }), true);
  for (const query of [{ tool: 'trajectory' }, { tool: ['sql', 'table'] }, { sql: 'DELETE FROM t' }, { restore: 'tab' }]) assert.equal(policy.previewRouteAllowed('sql', query), false);
});

test('Preview SQL rejects multiple statements, control plane and quoted semicolon bypasses', () => {
  for (const sql of ['SELECT "DeviceID" FROM "Table_Name"', "SELECT 'a;b'", 'SHOW TABLES;', 'DESCRIBE TABLE "Table_Name"', 'EXPLAIN SELECT * FROM t']) assert.equal(policy.previewSqlReadAllowed(sql), true, sql);
  for (const sql of ['DELETE FROM t', 'SELECT 1; INSERT INTO t VALUES(1)', 'SHOW USERS', 'SHOW TOKENS', 'EXPLAIN DELETE FROM t', '/*x*/ DELETE FROM t', 'SELECT 1 -- comment\n; DELETE FROM t']) assert.equal(policy.previewSqlReadAllowed(sql), false, sql);
});

test('Preview request boundary denies mutation, embedding, full object bytes and graph export', () => {
  for (const [method, path] of [['POST', '/v1/db/a/sql/batch'], ['POST', '/v1/db/a/vector/embed-preview'], ['GET', '/v1/db/a/s3/b/key'], ['GET', '/v1/db/a/graphs/g/operations/export'], ['POST', '/v1/db/a/mq/t/ack']]) {
    assert.equal(policy.previewRequestAction(method, new URL(path, window.location.origin), {}, new Headers()), null, path);
  }
  assert.equal(policy.previewRequestAction('GET', new URL('/v1/db/a/s3/b/key', window.location.origin), {}, new Headers({ Range: 'bytes=0-4095' })), null);
  assert.equal(policy.previewRequestAction('GET', new URL('/v1/db/a/s3/b/key', window.location.origin), {}, new Headers({ Range: 'bytes=0-4096' })), null);
});

test('Streaming limit refuses an oversized chunk and cancels before parsing', async () => {
  let cancelled = 0;
  const response = new Response(new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(9)); }, cancel() { cancelled++; } }));
  await assert.rejects(readPreviewBody(response, AbortSignal.timeout(1000), 8), /上限/);
  assert.equal(cancelled, 1);
});

test('Streaming limit accepts exact boundary and refuses advertised overflow', async () => {
  assert.equal((await readPreviewBody(new Response('12345678'), AbortSignal.timeout(1000), 8)).length, 8);
  await assert.rejects(readPreviewBody(new Response('1', { headers: { 'content-length': '9' } }), AbortSignal.timeout(1000), 8), /上限/);
});

function config(data = { sql: 'SHOW TABLES' }, extra = {}) {
  return { url: '/v1/db/a/sql', baseURL: '/', method: 'POST', headers: new AxiosHeaders({ Authorization: 'Bearer fixture' }), data: JSON.stringify(data), ...extra };
}
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });

test('Adapter serializes actual HTTP reads and clamps SQL output to 1000', async () => {
  session.selectPreviewDatabase('a', false);
  const old = globalThis.fetch; let concurrent = 0; let maximum = 0; let sent = 0;
  globalThis.fetch = async (url, options) => {
    concurrent++; maximum = Math.max(maximum, concurrent);
    await new Promise((resolve) => setTimeout(resolve, 5)); concurrent--;
    if (String(url).endsWith('/access')) return json({ canRead: true, canWrite: false });
    sent++; assert.equal(JSON.parse(options.body).previewMaxRows, 1000); return new Response('{"type":"meta","columns":["name"]}\n["A"]\n{"type":"end","rowCount":1,"recordsAffected":-1,"elapsedMs":1}\n');
  };
  try { await Promise.all([previewAdapter(config()), previewAdapter(config())]); assert.equal(maximum, 1); assert.equal(sent, 2); }
  finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Revoked permission clears context and refuses the pending payload before transmission', async () => {
  session.selectPreviewDatabase('a', true);
  const old = globalThis.fetch; let calls = 0;
  globalThis.fetch = async () => { calls++; return json({ canRead: true, canWrite: false }); };
  try { await assert.rejects(previewAdapter(config()), /权限/); assert.equal(calls, 1); assert.equal(session.previewSession.value.database, ''); assert.equal(session.previewSession.value.blocked, true); }
  finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Lost insert response remains unknown and performs exactly one send without replay', async () => {
  session.selectPreviewDatabase('a', true);
  const old = globalThis.fetch; let writes = 0;
  globalThis.fetch = async (url) => {
    if (String(url).endsWith('/access')) return json({ canRead: true, canWrite: true });
    writes++; throw new TypeError('Response lost after server commit');
  };
  try {
    const request = config({ sql: 'INSERT INTO t VALUES (@id)' }, { headers: new AxiosHeaders({ 'X-SonnetDB-Workbench-Action': 'relation.insert.one' }) });
    await assert.rejects(previewAdapter(request));
    assert.equal(writes, 1); assert.match(session.previewSession.value.message, /写入状态未知/); assert.equal(session.previewSession.value.blocked, true);
  } finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Input over 64 KiB and cross-origin requests fail without contacting Server', async () => {
  session.selectPreviewDatabase('a', false);
  const old = globalThis.fetch; globalThis.fetch = async () => { throw new Error('MUST NOT SEND'); };
  try {
    await assert.rejects(previewAdapter(config({ sql: "SELECT '" + 'x'.repeat(65536) + "'" })), /64 KiB/);
    await assert.rejects(previewAdapter(config({}, { url: 'https://other.invalid/v1/db/a/sql' })), /同源/);
  } finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Retained windows fail closed at 4 MiB instead of silently accumulating more data', () => {
  session.clearPreviewSession(); session.retainPreviewBytes(4 * 1024 * 1024, policy.previewLimits.bytes);
  assert.throws(() => session.retainPreviewBytes(1, policy.previewLimits.bytes), /4 MiB/); session.clearPreviewSession();
});

test('Scope R1 rejects every deferred dedicated model endpoint including small range reads', () => {
  for (const [method, route] of [['POST', 'documents/a/find'], ['POST', 'kv/keyspaces'], ['POST', 'kv/a/scan'], ['POST', 'fulltext/indexes'], ['POST', 'fulltext/search-preview'], ['POST', 'vector/indexes'], ['POST', 'vector/search-preview'], ['POST', 'mq/topics'], ['POST', 'mq/a/browse'], ['GET', 'graphs'], ['GET', 'graphs/g/operations/visualization'], ['GET', 's3/b/k']]) {
    assert.equal(policy.previewRequestAction(method, new URL(`/v1/db/a/${route}`, window.location.origin), {}, new Headers({ Range: 'bytes=0-1' })), null, route);
  }
});

test('Lost permission preflight clears data and sends no SQL', async () => {
  session.selectPreviewDatabase('a', true);
  const old = globalThis.fetch; let calls = 0;
  globalThis.fetch = async () => { calls++; throw new TypeError('offline'); };
  try { await assert.rejects(previewAdapter(config())); assert.equal(calls, 1); assert.equal(session.previewSession.value.blocked, true); }
  finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Incomplete and over-row SQL responses are discarded before result delivery', async () => {
  const old = globalThis.fetch;
  try {
    for (const body of ['{"type":"meta","columns":["x"]}\n[1]\n', '{"type":"meta","columns":["x"]}\n' + '[1]\n'.repeat(1001) + '{"type":"end","rowCount":1001,"recordsAffected":-1,"elapsedMs":1}\n']) {
      session.selectPreviewDatabase('a', false);
      globalThis.fetch = async (url) => String(url).endsWith('/access') ? json({ canRead: true, canWrite: false }) : new Response(body);
      await assert.rejects(previewAdapter(config()), /响应校验失败/);
      assert.equal(session.previewSession.value.blocked, true);
    }
  } finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Decoded container overhead is charged even when wire bytes fit', async () => {
  session.selectPreviewDatabase('a', false);
  const old = globalThis.fetch;
  globalThis.fetch = async (url) => String(url).endsWith('/access') ? json({ canRead: true, canWrite: false }) : new Response('[0,' + '0,'.repeat(80000) + '0]');
  try { await assert.rejects(previewAdapter(config()), /解码后载荷/); assert.equal(session.previewSession.value.blocked, true); }
  finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Late old permission response cannot clear a new database generation', async () => {
  session.selectPreviewDatabase('a', true);
  const old = globalThis.fetch; let release; let arrived;
  const received = new Promise((resolve) => { arrived = resolve; });
  globalThis.fetch = () => new Promise((resolve) => { release = resolve; arrived(); });
  try {
    const pending = previewAdapter(config()); const rejected = assert.rejects(pending);
    await received; session.selectPreviewDatabase('b', false); release(json({ canRead: false, canWrite: false })); await rejected;
    assert.equal(session.previewSession.value.database, 'b'); assert.equal(session.previewSession.value.blocked, false);
  } finally { globalThis.fetch = old; session.clearPreviewSession(); }
});

test('Unknown build profile refuses routes and a blocked stream observes cancellation', async () => {
  const module = new SourceTextModule(stripTypeScriptTypes(readFileSync(new URL('../src/preview/policy.ts', import.meta.url), 'utf8'), { mode: 'transform' }), { initializeImportMeta(meta) { meta.env = { VITE_WORKBENCH_PROFILE: 'unknown' }; } });
  await module.link(() => { throw new Error('Unexpected dependency'); }); await module.evaluate({ timeout: 1000 });
  assert.equal(module.namespace.previewSupported, false); assert.equal(module.namespace.previewRouteAllowed('sql', {}), false);
  const controller = new AbortController(); let cancelled = false;
  const response = new Response(new ReadableStream({ cancel() { cancelled = true; } }));
  const read = readPreviewBody(response, controller.signal); const rejected = assert.rejects(read);
  controller.abort(); await rejected; assert.equal(cancelled, true);
});

test('HTTP 500 after a single insert is unknown and never retried', async () => {
  session.selectPreviewDatabase('a', true); const old = globalThis.fetch; let writes = 0;
  globalThis.fetch = async (url) => {
    if (String(url).endsWith('/access')) return json({ canRead: true, canWrite: true });
    writes++; return json({ error: 'internal_error', message: 'failure after commit' }, 500);
  };
  try {
    await assert.rejects(previewAdapter(config({ sql: 'INSERT INTO t VALUES (@id)' }, { headers: new AxiosHeaders({ 'X-SonnetDB-Workbench-Action': 'relation.insert.one' }) })), /写入状态未知/);
    assert.equal(writes, 1); assert.match(session.previewSession.value.message, /写入状态未知/);
  } finally { globalThis.fetch = old; session.clearPreviewSession(); }
});
