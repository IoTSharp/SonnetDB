import * as assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { channel } from 'node:diagnostics_channel';
import { lstat, readFile, realpath, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import type { SqlResultSet } from '../../core/types';

interface Phase {
  name: string;
  command: string;
  document: string;
  start: number;
  end: number;
  sql: string;
  result: SqlResultSet;
}

interface Reference {
  schema: string;
  runId: string;
  baseUrl: string;
  database: string;
  label: string;
  phases: Phase[];
}

interface Payload {
  title: string;
  result: SqlResultSet;
  source: { label: string; text: string };
  context: { connectionLabel: string; database: string };
}

interface Transport {
  phase: string;
  method: string;
  path: string;
  status?: number;
}

/** 在真实 Extension Host 调用生产只读命令；公开 API 驱动与生成数据不等同实际 UI 渲染。 */
export async function run(): Promise<void> {
  const runRoot = required('SONNETDB_QUERY_REAL_RUN_ROOT');
  const token = required('SONNETDB_QUERY_REAL_TOKEN');
  const expectedHash = required('SONNETDB_QUERY_REAL_REFERENCE_SHA256');
  const referencePath = path.join(runRoot, 'reference.json');
  const secrets = [token, Buffer.from(token).toString('base64'), Buffer.from(token).toString('base64url'),
    Buffer.from(token).toString('hex'), Buffer.from(token).toString('hex').toUpperCase(), encodeURIComponent(token)];
  const deadline = Date.now() + 110_000;
  let currentPhase = 'initialization';
  let outcome = 'FAIL';
  let files = 0;
  let bytes = 0;
  let panel: vscode.WebviewPanel | undefined;
  const documents: vscode.TextDocument[] = [];
  const restores: Array<() => void> = [];
  const completed: string[] = [];
  const transport: Transport[] = [];
  const requestRecords = new WeakMap<object, Transport>();
  let transportOverflow = false;
  let inputCount = 0;
  let databasePicks = 0;
  let historyPicks = 0;
  let observedHistory: Array<{ label: string; description: string; detail: string }> = [];
  let observerReady = false;
  let cleanupErrors = 0;
  let restoredApis = 0;
  let failureType: string | null = null;

  const createChannel = channel('undici:request:create');
  const headersChannel = channel('undici:request:headers');
  const onCreate = (value: unknown): void => {
    if (!observerReady || !value || typeof value !== 'object') return;
    const request = (value as { request?: { origin?: string; path?: string; method?: string } }).request;
    if (!request || request.origin !== reference.baseUrl || request.path !== `/v1/db/${encodeURIComponent(reference.database)}/sql`) return;
    if (transport.length >= 12) { transportOverflow = true; return; }
    const record: Transport = { phase: currentPhase, method: String(request.method), path: String(request.path) };
    transport.push(record);
    requestRecords.set(request, record);
  };
  const onHeaders = (value: unknown): void => {
    if (!value || typeof value !== 'object') return;
    const item = value as { request?: object; response?: { statusCode?: number } };
    const record = item.request && requestRecords.get(item.request);
    if (record) record.status = item.response?.statusCode;
  };
  let reference: Reference;

  try {
    assert.equal(process.platform, 'win32');
    assert.ok(path.isAbsolute(runRoot));
    assert.equal((await realpath(runRoot)).toLowerCase(), path.resolve(runRoot).toLowerCase());
    assert.equal((await lstat(runRoot)).isSymbolicLink(), false);
    assert.equal(path.dirname(runRoot).toLowerCase(), 'd:\\source\\sonnetdb\\artifacts\\wb42-validation-20261007');
    const raw = await readFile(referencePath);
    assert.ok(raw.length <= 512 * 1024);
    assert.equal(createHash('sha256').update(raw).digest('hex').toUpperCase(), expectedHash.toUpperCase());
    reference = JSON.parse(raw.toString('utf8')) as Reference;
    assert.equal(reference.schema, 'sonnetdb.wb42.reference.v1');
    assert.equal(reference.runId, path.basename(runRoot));
    assert.equal(reference.baseUrl, 'http://127.0.0.1:18342');
    assert.equal(reference.database, 'Workbench42');
    assert.equal(reference.phases.length, 3);

    const extension = vscode.extensions.getExtension('iotsharp.sonnetdb-vscode');
    assert.ok(extension);
    await bounded(extension.activate(), 20_000);
    assert.equal(extension.isActive, true);
    const registered = new Set(await bounded(vscode.commands.getCommands(true), 10_000));
    for (const name of ['sonnetdb.addConnection', 'sonnetdb.selectDatabase', 'sonnetdb.runQuery', 'sonnetdb.runSelection', 'sonnetdb.explainQuery', 'sonnetdb.showQueryHistory']) {
      assert.ok(registered.has(name));
    }

    replace(vscode.window, 'showInputBox', async (options?: vscode.InputBoxOptions) => {
      assert.ok(Date.now() < deadline);
      inputCount += 1;
      assert.ok(inputCount <= 3);
      if (inputCount === 1) { assert.equal(options?.prompt, 'Connection label'); return reference.label; }
      if (inputCount === 2) { assert.equal(options?.prompt, 'SonnetDB base URL'); return reference.baseUrl; }
      assert.equal(options?.prompt, 'Bearer token (stored in SecretStorage)');
      assert.equal(options?.password, true);
      return token;
    });
    replace(vscode.window, 'showQuickPick', async (items: unknown, options?: vscode.QuickPickOptions) => {
      assert.ok(Date.now() < deadline);
      const entries = await Promise.resolve(items) as unknown[];
      assert.ok(Array.isArray(entries) && entries.length <= 50);
      if (options?.placeHolder === 'Select the active database') {
        databasePicks += 1;
        assert.equal(databasePicks, 1);
        assert.ok(entries.includes(reference.database));
        return reference.database;
      }
      assert.equal(options?.placeHolder, 'Restore a SonnetDB query from local history');
      historyPicks += 1;
      assert.ok(historyPicks <= 20);
      observedHistory = entries.map((value) => {
        assert.ok(value && typeof value === 'object');
        const item = value as { label?: string; description?: string; detail?: string };
        assert.equal(typeof item.label, 'string');
        assert.equal(typeof item.description, 'string');
        assert.equal(typeof item.detail, 'string');
        return { label: item.label!, description: item.description!, detail: item.detail! };
      });
      return undefined;
    });
    const originalCreate = vscode.window.createWebviewPanel;
    replace(vscode.window, 'createWebviewPanel', (...args: Parameters<typeof originalCreate>) => {
      assert.equal(args[0], 'sonnetdb.queryResult');
      assert.equal(panel, undefined, 'The production result panel must be reused.');
      panel = originalCreate.apply(vscode.window, args);
      return panel;
    });

    await command('sonnetdb.addConnection');
    assert.equal(inputCount, 3);
    await command('sonnetdb.selectDatabase');
    assert.equal(databasePicks, 1);
    createChannel.subscribe(onCreate);
    headersChannel.subscribe(onHeaders);
    observerReady = true;

    for (let index = 0; index < reference.phases.length && index < 3; index += 1) {
      assert.ok(Date.now() < deadline);
      const phase = reference.phases[index];
      currentPhase = phase.name;
      assert.ok(['current-statement', 'exact-selection', 'explain'].includes(phase.name));
      assert.ok(['sonnetdb.runQuery', 'sonnetdb.runSelection', 'sonnetdb.explainQuery'].includes(phase.command));
      assert.ok(/^(SELECT|EXPLAIN SELECT) /u.test(phase.sql));
      const document = await bounded(vscode.workspace.openTextDocument({ language: 'sql', content: phase.document }), 10_000);
      documents.push(document);
      const editor = await bounded(vscode.window.showTextDocument(document, vscode.ViewColumn.One), 10_000);
      editor.selection = new vscode.Selection(document.positionAt(phase.start), document.positionAt(phase.end));
      assert.equal(vscode.window.activeTextEditor?.document.uri.toString(), document.uri.toString());
      const before = transport.length;
      await command(phase.command);
      assert.ok(panel);
      const html = (panel as vscode.WebviewPanel).webview.html;
      assert.ok(Buffer.byteLength(html, 'utf8') <= 512 * 1024);
      const encoded = /<script type="application\/json" id="payload" nonce="[^"]+">([A-Za-z0-9+/=]+)<\/script>/u.exec(html)?.[1];
      assert.ok(encoded);
      const payload = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) as Payload;
      assert.equal(payload.source.label, 'Query');
      assert.equal(payload.source.text, phase.sql);
      assert.deepEqual(payload.context, { connectionLabel: reference.label, database: reference.database });
      assert.deepEqual(payload.result.columns, phase.result.columns);
      assert.deepEqual(payload.result.rows, phase.result.rows);
      assert.equal(payload.result.error, null);
      assert.equal(payload.result.hasColumns, true);
      assert.ok(payload.result.end);
      assert.equal(payload.result.end.rowCount, phase.result.end?.rowCount);
      assert.equal(payload.result.end.recordsAffected, phase.result.end?.recordsAffected);
      assert.ok(payload.result.rows.length <= 32);
      const actualRequests = transport.slice(before);
      assert.equal(actualRequests.length, 1);
      assert.deepEqual(actualRequests[0], { phase: phase.name, method: 'POST', path: `/v1/db/${reference.database}/sql`, status: 200 });
      assert.equal(transportOverflow, false);
      await evidence(`phase-${index + 1}.json`, { schema: 'sonnetdb.wb42.host-phase.v1', runId: reference.runId,
        phase: phase.name, command: phase.command, editor: { document: phase.document, selection: [phase.start, phase.end] },
        actualRequest: actualRequests[0], payload, renderedWebviewVerified: false, observedAtUtc: new Date().toISOString() });
      completed.push(phase.name);
      console.log(`WB42 completed ${phase.name}.`);
    }

    currentPhase = 'history';
    const historyDeadline = Math.min(deadline, Date.now() + 10_000);
    for (let attempt = 0; attempt < 20 && Date.now() < historyDeadline; attempt += 1) {
      await command('sonnetdb.showQueryHistory');
      if (observedHistory.length === 3) break;
      await new Promise((resolve) => setTimeout(resolve, 500));
    }
    assert.equal(observedHistory.length, 3);
    for (let index = 0; index < 3; index += 1) {
      const phase = reference.phases[2 - index];
      const item = observedHistory[index];
      assert.equal(item.label, phase.sql.split(/\r?\n/u)[0].slice(0, 100));
      assert.ok(item.description.startsWith(`${phase.result.end?.rowCount} rows · `));
      const context = `${reference.label} / ${reference.database}`;
      assert.ok(item.detail === context || item.detail.startsWith(`${context} / `));
      assert.equal(item.description.includes('failed'), false);
    }
    await evidence('history.json', { schema: 'sonnetdb.wb42.public-history.v1', runId: reference.runId,
      entries: observedHistory, promptDriver: true, historyUiVerified: false, observedAtUtc: new Date().toISOString() });
    outcome = 'PASS';
  } catch (error) {
    failureType = error instanceof Error && ['AssertionError', 'TypeError', 'Error'].includes(error.name) ? error.name : 'unclassified';
    throw error;
  } finally {
    observerReady = false;
    createChannel.unsubscribe(onCreate);
    headersChannel.unsubscribe(onHeaders);
    for (let index = restores.length - 1; index >= 0 && index < 4; index -= 1) {
      try { restores[index](); restoredApis += 1; } catch { outcome = 'FAIL'; cleanupErrors += 1; }
    }
    try { (panel as vscode.WebviewPanel | undefined)?.dispose(); } catch { outcome = 'FAIL'; cleanupErrors += 1; }
    for (let index = 0; index < documents.length && index < 3; index += 1) {
      try {
        await bounded(vscode.window.showTextDocument(documents[index], vscode.ViewColumn.One), 3_000);
        await bounded(vscode.commands.executeCommand('workbench.action.closeActiveEditor'), 3_000);
      } catch { outcome = 'FAIL'; cleanupErrors += 1; }
    }
    await evidence('host-result.json', { schema: 'sonnetdb.wb42.host-result.v1', runId: path.basename(runRoot),
      outcome, completed, stoppedAtPhase: currentPhase, failureType, apiRestored: restoredApis === 3, cleanupErrors,
      commandTimeoutMilliseconds: 20_000, productionFetchTimeoutVerified: false,
      transport, promptDriver: true, generatedPayloadOnly: true, finishedAtUtc: new Date().toISOString() });
  }
  assert.equal(outcome, 'PASS', 'WB42 public API cleanup must succeed.');

  async function command(name: string): Promise<void> {
    assert.ok(Date.now() < deadline);
    await bounded(vscode.commands.executeCommand(name), Math.min(20_000, deadline - Date.now()));
  }

  function replace(target: object, property: string, value: unknown): void {
    const descriptor = Object.getOwnPropertyDescriptor(target, property);
    assert.ok(descriptor && (descriptor.configurable || descriptor.writable), `Public VS Code API ${property} must support a reversible driver.`);
    if (descriptor.configurable) Object.defineProperty(target, property, { configurable: true, enumerable: descriptor.enumerable, writable: true, value });
    else Reflect.set(target, property, value);
    restores.push(() => { Object.defineProperty(target, property, descriptor); });
  }

  async function evidence(name: string, value: unknown): Promise<void> {
    assert.ok(/^(phase-[1-3]|history|host-result)\.json$/u.test(name));
    const text = `${JSON.stringify(value, null, 2)}\n`;
    for (const secret of secrets) assert.equal(text.includes(secret), false, 'A runtime secret must never enter evidence.');
    const size = Buffer.byteLength(text);
    files += 1; bytes += size;
    assert.ok(files <= 5 && size <= 512 * 1024 && bytes <= 3 * 1024 * 1024);
    await writeFile(path.join(runRoot, name), text, { flag: 'wx' });
  }
}

function required(name: string): string {
  const value = process.env[name]?.trim();
  assert.ok(value, `Required isolated test configuration is missing: ${name}.`);
  return value;
}

async function bounded<T>(promise: Thenable<T>, milliseconds: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([Promise.resolve(promise), new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => reject(new Error('WB42 public command deadline exceeded.')), milliseconds);
    })]);
  } finally { if (timer) clearTimeout(timer); }
}
