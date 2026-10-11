import assert from 'node:assert/strict';
import Module from 'node:module';
import test from 'node:test';
import { runInNewContext } from 'node:vm';
import type * as vscode from 'vscode';
import type { QueryHistoryEntry, QueryHistoryStorage } from '../core/queryHistory';
import { parseNdjson, SonnetDbClient } from '../core/sonnetdbClient';
import type { SqlResultSet } from '../core/types';

// 首个测试是单 end、1 秒上限的微试；其余输入批次同时受项目数与墙钟约束。
test('one native end provides the canonical elapsed alias', { timeout: 1_000 }, () => {
  const result = parseNdjson('{"type":"end","rowCount":2,"recordsAffected":-1,"elapsedMilliseconds":24.7995}');
  assert.equal(result.end?.elapsedMs, 24.7995);
  assert.deepEqual(result.end, { type: 'end', rowCount: 2, recordsAffected: -1,
    elapsedMilliseconds: 24.7995, elapsedMs: 24.7995 });
});

function endBody(elapsed: Record<string, unknown>): string {
  return JSON.stringify({ type: 'end', rowCount: 2, recordsAffected: -1, truncated: false, ...elapsed });
}

test('native, legacy and equal dual fields preserve finite nonnegative values without coercion', { timeout: 1_000 }, () => {
  const cases = [
    { input: { elapsedMilliseconds: 0 }, expected: 0 },
    { input: { elapsedMilliseconds: 1e308 }, expected: 1e308 },
    { input: { elapsedMs: 0 }, expected: 0 },
    { input: { elapsedMs: 0.4 }, expected: 0.4 },
    { input: { elapsedMilliseconds: 7.125, elapsedMs: 7.125 }, expected: 7.125 },
  ];
  const deadline = Date.now() + 500;
  let checked = 0;
  for (let index = 0; index < cases.length && index < 5 && Date.now() < deadline; index += 1) {
    const { input, expected } = cases[index];
    const result = parseNdjson(endBody(input));
    assert.equal(result.end?.elapsedMs, expected);
    assert.deepEqual(result.end, { type: 'end', rowCount: 2, recordsAffected: -1,
      truncated: false, ...input, elapsedMs: expected });
    checked += 1;
  }
  assert.equal(checked, cases.length, 'Every bounded valid case must run.');
});

test('missing, unsupported, invalid and conflicting elapsed metadata remains unknown', { timeout: 2_000 }, () => {
  const cases: Array<Record<string, unknown>> = [
    {}, { elapsed: 7 }, { ElapsedMilliseconds: 7 },
    { elapsedMilliseconds: null }, { elapsedMilliseconds: '7' },
    { elapsedMilliseconds: true }, { elapsedMilliseconds: -1 },
    { elapsedMilliseconds: [] }, { elapsedMilliseconds: {} },
    { elapsedMs: null }, { elapsedMs: '7' }, { elapsedMs: false },
    { elapsedMs: -0.1 }, { elapsedMs: [] }, { elapsedMs: {} },
    { elapsedMilliseconds: 7, elapsedMs: 8 },
    { elapsedMilliseconds: null, elapsedMs: 7 },
    { elapsedMilliseconds: 7, elapsedMs: '7' },
    { elapsedMilliseconds: -1, elapsedMs: 7 },
    { elapsedMilliseconds: 7, elapsedMs: -1 },
  ];
  const deadline = Date.now() + 1_000;
  let checked = 0;
  for (let index = 0; index < cases.length && index < 20 && Date.now() < deadline; index += 1) {
    const input = cases[index];
    const result = parseNdjson(endBody(input));
    const retained = { ...input };
    delete retained.elapsedMs;
    assert.equal(result.end?.elapsedMs, undefined);
    assert.equal(Object.hasOwn(result.end!, 'elapsedMs'), false);
    assert.deepEqual(result.end, { type: 'end', rowCount: 2, recordsAffected: -1,
      truncated: false, ...retained });
    checked += 1;
  }
  assert.equal(checked, cases.length, 'Every bounded unknown case must run.');
  // JSON 允许大指数；JSON.parse 会得到 Infinity，但它仍不能成为规范耗时。
  const infinite = parseNdjson('{"type":"end","rowCount":2,"recordsAffected":-1,"elapsedMilliseconds":1e309,"elapsedMs":7}');
  assert.equal((infinite.end as unknown as Record<string, unknown>).elapsedMilliseconds, Infinity);
  assert.equal(Object.hasOwn(infinite.end!, 'elapsedMs'), false);
  const legacyInfinite = parseNdjson('{"type":"end","rowCount":2,"recordsAffected":-1,"elapsedMs":1e309}');
  assert.equal(Object.hasOwn(legacyInfinite.end!, 'elapsedMs'), false);
});

test('elapsed normalization preserves result frames, errors and the last end record', { timeout: 1_000 }, () => {
  const end = { type: 'end', rowCount: 2, recordsAffected: -1, elapsedMilliseconds: 2.6558,
    truncated: false, extra: { marker: 'unchanged' } };
  const result = parseNdjson([
    'not JSON', 'null', '42',
    JSON.stringify({ type: 'meta', columns: ['DeviceID', 'MixedCaseName'] }),
    JSON.stringify([2, 'left']), JSON.stringify([5, 'right']),
    endBody({ elapsedMs: 99 }), JSON.stringify(end),
    JSON.stringify({ type: 'error', code: 'read_only', message: 'unchanged error' }),
  ].join('\r\n'));
  assert.deepEqual(result, { columns: ['DeviceID', 'MixedCaseName'], rows: [[2, 'left'], [5, 'right']],
    hasColumns: true, end: { ...end, elapsedMs: 2.6558 },
    error: { code: 'read_only', message: 'unchanged error' } });
  assert.equal(parseNdjson('{"type":"meta","columns":[]}').end, null);
});

interface DomNode {
  textContent: string;
  innerHTML: string;
  className: string;
  hidden: boolean;
  children: DomNode[];
  appendChild(child: DomNode): void;
  replaceChildren(...children: DomNode[]): void;
}

function domNode(): DomNode {
  return { textContent: '', innerHTML: '', className: '', hidden: true, children: [],
    appendChild(child) { this.children.push(child); },
    replaceChildren(...children) { this.children = children; } };
}

function executeWebview(html: string): { summary: string; sourceTitle: string; sourceText: string; raw: unknown } {
  const encoded = /id="payload" nonce="[^"]+">([A-Za-z0-9+/=]+)<\/script>/u.exec(html)?.[1];
  const script = /<script nonce="[^"]+">([\s\S]*?)<\/script>/u.exec(html)?.[1];
  assert.ok(encoded); assert.ok(script);
  const payload = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) as {
    result: SqlResultSet; raw: unknown; source: { label: string; text: string };
    context: { connectionLabel: string; database: string };
  };
  assert.deepEqual(payload.source, { label: 'Query', text: 'SELECT "MixedCaseName" FROM "DeviceID_Main";' });
  assert.deepEqual(payload.context, { connectionLabel: 'elapsed fixture', database: 'MixedCaseDb' });
  const nodes = new Map<string, DomNode>();
  let created = 0;
  const nodeDeadline = Date.now() + 1_000;
  const document = {
    getElementById: (id: string): DomNode => {
      assert.ok(nodes.size < 12 || nodes.has(id), 'The fixture owns at most twelve DOM IDs.');
      assert.ok(Date.now() < nodeDeadline);
      if (!nodes.has(id)) nodes.set(id, domNode());
      return nodes.get(id)!;
    },
    querySelectorAll: () => [],
    createElement: (): DomNode => {
      created += 1;
      assert.ok(created <= 16 && Date.now() < nodeDeadline, 'The fixture creates at most sixteen nodes in one second.');
      return domNode();
    },
  };
  document.getElementById('payload').textContent = encoded;
  // 执行面板生成的完整浏览器脚本，不复制摘要计算。没有真实浏览器或 Extension Host。
  runInNewContext(script, { document, TextDecoder, Uint8Array,
    atob: (value: string) => Buffer.from(value, 'base64').toString('binary'),
    acquireVsCodeApi: () => ({ postMessage: () => undefined }) }, { timeout: 1_000 });
  const renderedRaw = document.getElementById('raw').children[0]?.textContent;
  assert.ok(renderedRaw);
  assert.deepEqual(JSON.parse(renderedRaw), payload.raw);
  return { summary: document.getElementById('summary').textContent,
    sourceTitle: document.getElementById('source-title').textContent,
    sourceText: document.getElementById('source-text').textContent, raw: payload.raw };
}

class MetadataStorage implements QueryHistoryStorage {
  public entries: QueryHistoryEntry[] = [];
  public updates = 0;

  public get<T>(key: string, defaultValue: T): T {
    assert.equal(key, 'sonnetdb.queryHistory');
    return (this.entries ?? defaultValue) as T;
  }

  public async update(key: string, value: unknown): Promise<void> {
    assert.equal(key, 'sonnetdb.queryHistory');
    assert.ok(Array.isArray(value));
    this.updates += 1;
    this.entries = value as QueryHistoryEntry[];
  }
}

test('real client parser, panel history and generated script share canonical elapsed metadata', { timeout: 5_000 }, async () => {
  let html = '';
  const disposable = { dispose: () => undefined };
  const vscodeStub = {
    commands: { registerCommand: () => disposable },
    window: { createWebviewPanel: () => ({ title: '', reveal: () => undefined,
      onDidDispose: () => disposable, webview: { get html() { return html; },
        set html(value: string) { html = value; }, onDidReceiveMessage: () => disposable } }) },
    ViewColumn: { Beside: 2 },
  };
  const loader = Module as unknown as { _load: (request: string, parent?: unknown, isMain?: boolean) => unknown };
  const originalLoad = loader._load;
  const originalFetch = globalThis.fetch;
  let QueryResultPanel: typeof import('../panels/queryResultPanel').QueryResultPanel;
  try {
    loader._load = (request, parent, isMain) => request === 'vscode'
      ? vscodeStub : originalLoad.call(Module, request, parent, isMain);
    ({ QueryResultPanel } = require('../panels/queryResultPanel') as typeof import('../panels/queryResultPanel'));
  } finally {
    loader._load = originalLoad;
  }
  const cases: Array<{ elapsed: Record<string, unknown>; expected?: number }> = [
    { elapsed: { elapsedMilliseconds: 24.7995 }, expected: 24.7995 },
    { elapsed: { elapsedMs: 0.4 }, expected: 0.4 },
    { elapsed: { elapsedMilliseconds: 0, elapsedMs: 0 }, expected: 0 },
    { elapsed: {} },
    { elapsed: { elapsedMilliseconds: -1 } },
    { elapsed: { elapsedMilliseconds: null, elapsedMs: 7 } },
    { elapsed: { elapsedMilliseconds: 7, elapsedMs: 8 } },
  ];
  let checked = 0;
  const deadline = Date.now() + 3_000;
  try {
    for (let index = 0; index < cases.length && index < 7 && Date.now() < deadline; index += 1) {
      const { elapsed, expected } = cases[index];
      let requests = 0;
      globalThis.fetch = async (input, init) => {
        requests += 1;
        assert.equal(input, 'http://elapsed.invalid/v1/db/MixedCaseDb/sql');
        assert.equal(init?.method, 'POST');
        assert.deepEqual(JSON.parse(String(init?.body)), { sql: 'SELECT "MixedCaseName" FROM "DeviceID_Main";' });
        return new Response([JSON.stringify({ type: 'meta', columns: ['MixedCaseName'] }),
          JSON.stringify(['left']), JSON.stringify(['right']), endBody(elapsed)].join('\n'),
        { status: 200, headers: { 'Content-Type': 'application/x-ndjson' } });
      };
      const result = await new SonnetDbClient('http://elapsed.invalid').executeSql('MixedCaseDb',
        'SELECT "MixedCaseName" FROM "DeviceID_Main";');
      const storage = new MetadataStorage();
      const context = { subscriptions: [], globalState: storage } as unknown as vscode.ExtensionContext;
      const panel = new QueryResultPanel(context);
      await panel.show(result, 'SELECT "MixedCaseName" FROM "DeviceID_Main";', 'elapsed fixture', 'MixedCaseDb');
      assert.equal(requests, 1); assert.equal(storage.updates, 1); assert.equal(storage.entries.length, 1);
      const entry = storage.entries[0];
      assert.equal(entry.elapsedMs, expected); assert.equal(entry.rowCount, 2); assert.equal(entry.failed, false);
      assert.equal(entry.sql, 'SELECT "MixedCaseName" FROM "DeviceID_Main";');
      assert.equal(entry.connectionLabel, 'elapsed fixture'); assert.equal(entry.database, 'MixedCaseDb');
      const rendered = executeWebview(html);
      assert.equal(rendered.summary, `2 rows${expected === undefined ? '' : ` · ${expected} ms`} · elapsed fixture / MixedCaseDb`);
      assert.equal(rendered.sourceTitle, 'Query'); assert.equal(rendered.sourceText, entry.sql);
      assert.deepEqual(rendered.raw, result);
      checked += 1;
    }
    assert.equal(checked, cases.length, 'Every bounded client-to-panel metadata case must run.');
  } finally {
    globalThis.fetch = originalFetch;
    loader._load = originalLoad;
  }
});
