import * as assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { channel } from 'node:diagnostics_channel';
import { lstat, readFile, realpath, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { QueryHistoryObservation } from '../../core/queryHistoryObservation';
import type { SqlResultSet } from '../../core/types';

/** Server 实际 NDJSON end DTO；生产静态类型的 elapsedMs 不能替代真实字段。 */
interface RealEnd {
  type: 'end';
  rowCount: number;
  recordsAffected: number;
  elapsedMilliseconds: number;
}

interface RealResult extends Omit<SqlResultSet, 'end'> {
  end: RealEnd | null;
}

interface Phase {
  name: string;
  command: string;
  document: string;
  start: number;
  end: number;
  sql: string;
  result: RealResult;
}

interface Reference {
  schema: string;
  slice: string;
  previewRequestShape: string;
  previewContract: { requestBodyKeys: string[]; previewMaxRowsSent: boolean; rowLimit: number; source: string; serverFullResult: boolean };
  runId: string;
  baseUrl: string;
  database: string;
  label: string;
  phases: Phase[];
}

interface Payload {
  title: string;
  result: RealResult;
  source: { label: string; text: string };
  context: { connectionLabel: string; database: string };
}

interface Transport {
  phase: string;
  method: string;
  path: string;
  status?: number;
}

type Check = 'initialization' | 'activation' | 'prompt-driver' | 'command' | 'panel' | 'html' | 'encoded'
  | 'decode' | 'source' | 'context' | 'columns' | 'rows' | 'end' | 'transport' | 'history' | 'evidence' | 'cleanup';

interface ObservationProgress {
  commandCompleted: boolean;
  panelPresent: boolean;
  htmlBytes: number | null;
  encodedPresent: boolean;
  encodedBytes: number | null;
  decoded: boolean;
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
  const historyObservation = new QueryHistoryObservation(deadline);
  let currentPhase = 'initialization';
  let currentCheck: Check = 'initialization';
  let progress: ObservationProgress = freshProgress();
  let failedObservationWritten = false;
  let evidenceRootVerified = false;
  let currentCommand: string | null = null;
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
  let historyCommandAttempts = 0;
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
    const record: Transport = { phase: currentPhase, method: request.method === 'POST' ? 'POST' : 'unexpected', path: `/v1/db/${encodeURIComponent(reference.database)}/sql` };
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
    assert.equal(path.dirname(runRoot).toLowerCase(), 'd:\\source\\sonnetdb\\artifacts\\wb57-validation-20261007');
    evidenceRootVerified = true;
    const referenceStat = await lstat(referencePath);
    assert.equal(referenceStat.isSymbolicLink(), false);
    assert.ok(referenceStat.isFile() && referenceStat.size <= 512 * 1024);
    const raw = await readFile(referencePath);
    assert.ok(raw.length <= 512 * 1024);
    assert.equal(createHash('sha256').update(raw).digest('hex').toUpperCase(), expectedHash.toUpperCase());
    reference = JSON.parse(raw.toString('utf8')) as Reference;
    assert.equal(reference.schema, 'sonnetdb.wb42.reference.v1');
    assert.equal(reference.slice, 'WB57');
    assert.equal(reference.previewRequestShape, 'sql-only');
    assert.deepEqual(reference.previewContract, { requestBodyKeys: ['sql'], previewMaxRowsSent: false, rowLimit: 100,
      source: 'diagnostic-admission', serverFullResult: true });
    assert.equal(reference.runId, path.basename(runRoot));
    assert.equal(reference.baseUrl, 'http://127.0.0.1:18358');
    assert.equal(reference.database, 'Workbench57');
    assert.equal(reference.label, 'WB57 isolated real Server');
    assert.equal(reference.phases.length, 3);
    for (let index = 0; index < 3; index += 1) {
      assert.ok(Date.now() < deadline);
      const phase = reference.phases[index];
      assert.ok(phase.sql.length <= 4096 && phase.document.length <= 16 * 1024);
      assert.ok(Array.isArray(phase.result.columns) && phase.result.columns.length <= 16);
      assert.ok(phase.result.columns.every((column) => typeof column === 'string' && column.length <= 4096));
      assert.ok(Array.isArray(phase.result.rows) && phase.result.rows.length <= 100);
      assert.ok(phase.result.rows.every((row) => Array.isArray(row) && row.length <= 16));
    }

    currentCheck = 'activation';
    const extension = vscode.extensions.getExtension('iotsharp.sonnetdb-vscode');
    assert.ok(extension);
    await bounded(extension.activate(), 20_000);
    assert.equal(extension.isActive, true);
    const registered = new Set(await bounded(vscode.commands.getCommands(true), 10_000));
    for (const name of ['sonnetdb.addConnection', 'sonnetdb.selectDatabase', 'sonnetdb.runQuery', 'sonnetdb.runSelection', 'sonnetdb.explainQuery', 'sonnetdb.showQueryHistory']) {
      assert.ok(registered.has(name));
    }

    currentCheck = 'prompt-driver';
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
        assert.ok(item.label!.length <= 100 && item.description!.length <= 160 && item.detail!.length <= 256);
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
    const originalShowError = vscode.window.showErrorMessage;
    replace(vscode.window, 'showErrorMessage', (...args: Parameters<typeof originalShowError>) =>
      historyObservation.forwardError(args[0], () => originalShowError.apply(vscode.window, args)));

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
      assert.ok(['current-statement', 'exact-selection', 'explain'].includes(phase.name));
      currentPhase = phase.name;
      progress = freshProgress();
      currentCheck = 'command';
      assert.ok(['sonnetdb.runQuery', 'sonnetdb.runSelection', 'sonnetdb.explainQuery'].includes(phase.command));
      assert.ok(/^(SELECT|EXPLAIN SELECT) /u.test(phase.sql));
      const document = await bounded(vscode.workspace.openTextDocument({ language: 'sql', content: phase.document }), 10_000);
      documents.push(document);
      const editor = await bounded(vscode.window.showTextDocument(document, vscode.ViewColumn.One), 10_000);
      editor.selection = new vscode.Selection(document.positionAt(phase.start), document.positionAt(phase.end));
      assert.equal(vscode.window.activeTextEditor?.document.uri.toString(), document.uri.toString());
      const before = transport.length;
      historyObservation.begin(phase.name);
      await command(phase.command);
      progress.commandCompleted = true;
      historyObservation.endQuery();
      // 单次公开标签快照使用原 20 次 history 命令预算；不证明持久化 ack 或 WB57 失败因果。
      await observePhaseHistory(phase);
      currentCheck = 'panel';
      progress.panelPresent = Boolean(panel);
      assert.ok(panel);
      currentCheck = 'html';
      const html = (panel as vscode.WebviewPanel).webview.html;
      progress.htmlBytes = Buffer.byteLength(html, 'utf8');
      assert.ok(progress.htmlBytes <= 512 * 1024);
      currentCheck = 'encoded';
      const encoded = /<script type="application\/json" id="payload" nonce="[^"]+">([A-Za-z0-9+/=]+)<\/script>/u.exec(html)?.[1];
      progress.encodedPresent = Boolean(encoded);
      progress.encodedBytes = encoded ? Buffer.byteLength(encoded, 'ascii') : null;
      assert.ok(encoded);
      currentCheck = 'decode';
      const payload = JSON.parse(Buffer.from(encoded, 'base64').toString('utf8')) as Payload;
      progress.decoded = true;
      // 先保存受白名单保护的实际/独立参考观察；这不表示任何比较或 phase 已通过。
      currentCheck = 'evidence';
      await evidence(`observation-${index + 1}.json`, { schema: 'sonnetdb.wb42.host-observation.v1', slice: 'WB57',
        runId: reference.runId, phase: phase.name, observedBeforeCheck: 'source', progress,
        actual: safePayload(payload, phase), reference: safePayload({ title: 'SonnetDB Query Result', result: phase.result,
          source: { label: 'Query', text: phase.sql }, context: { connectionLabel: reference.label, database: reference.database } }, phase),
        transport: transport.slice(before), comparisonOutcome: 'NOT_RUN', renderedWebviewVerified: false,
        observedAtUtc: new Date().toISOString() });
      currentCheck = 'source';
      assert.equal(payload.title, 'SonnetDB Query Result');
      assert.equal(payload.source.label, 'Query');
      assert.equal(payload.source.text, phase.sql);
      currentCheck = 'context';
      assert.deepEqual(payload.context, { connectionLabel: reference.label, database: reference.database });
      currentCheck = 'columns';
      assert.deepEqual(payload.result.columns, phase.result.columns);
      currentCheck = 'rows';
      assert.deepEqual(payload.result.rows, phase.result.rows);
      assert.ok(payload.result.rows.length <= 100);
      currentCheck = 'end';
      assert.equal(payload.result.error, null);
      assert.equal(payload.result.hasColumns, true);
      assert.ok(payload.result.end);
      assert.equal(payload.result.end.type, 'end');
      assert.equal(payload.result.end.rowCount, phase.result.end?.rowCount);
      assert.equal(payload.result.end.recordsAffected, phase.result.end?.recordsAffected);
      assert.ok(Number.isFinite(payload.result.end.elapsedMilliseconds));
      currentCheck = 'transport';
      const actualRequests = transport.slice(before);
      assert.equal(actualRequests.length, 1);
      assert.deepEqual(actualRequests[0], { phase: phase.name, method: 'POST', path: `/v1/db/${reference.database}/sql`, status: 200 });
      assert.equal(transportOverflow, false);
      currentCheck = 'evidence';
      await evidence(`phase-${index + 1}.json`, { schema: 'sonnetdb.wb42.host-phase.v1', slice: 'WB57', runId: reference.runId,
        phase: phase.name, command: phase.command, editor: { document: phase.document, selection: [phase.start, phase.end] },
        actualRequest: actualRequests[0], payload: { title: payload.title, source: payload.source, context: payload.context,
          result: { columns: payload.result.columns, rows: payload.result.rows, error: null, hasColumns: true,
            end: { type: payload.result.end.type, rowCount: payload.result.end.rowCount,
              recordsAffected: payload.result.end.recordsAffected, elapsedMilliseconds: payload.result.end.elapsedMilliseconds } } },
        queryHistoryObservation: historyObservation.snapshot(),
        renderedWebviewVerified: false, observedAtUtc: new Date().toISOString() });
      completed.push(phase.name);
      console.log(`WB57 completed ${phase.name}.`);
    }

    currentPhase = 'history';
    currentCheck = 'history';
    progress = freshProgress();
    const historyDeadline = Math.min(deadline, Date.now() + 10_000);
    for (let attempt = 0; attempt < 20 && historyCommandAttempts < 20 && historyPicks < 20 && Date.now() < historyDeadline; attempt += 1) {
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
    currentCheck = 'evidence';
    await evidence('history.json', { schema: 'sonnetdb.wb42.public-history.v1', slice: 'WB57', runId: reference.runId,
      entries: observedHistory, promptDriver: true, historyUiVerified: false, observedAtUtc: new Date().toISOString() });
    outcome = 'PASS';
  } catch (error) {
    failureType = error instanceof Error && ['AssertionError', 'TypeError', 'Error'].includes(error.name) ? error.name : 'unclassified';
    try {
      await evidence('failure-observation.json', { schema: 'sonnetdb.wb42.host-failure-observation.v1', slice: 'WB57',
        runId: path.basename(runRoot), phase: currentPhase, check: currentCheck, command: currentCommand, failureType, progress, completed,
        history: { pickCount: historyPicks, entryCount: observedHistory.length }, transport,
        queryHistoryObservation: historyObservation.snapshot(), historyCommandAttempts,
        phaseOutcome: 'FAIL', observedAtUtc: new Date().toISOString() });
      failedObservationWritten = true;
    } catch { /* 证据写失败保持 FAIL，仍继续 finally 恢复所有公开 API。 */ }
    throw new Error(`WB57 known-check failure: ${currentCheck}/${failureType}.`);
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
    await evidence('host-result.json', { schema: 'sonnetdb.wb42.host-result.v1', slice: 'WB57', runId: path.basename(runRoot),
      outcome, completed, stoppedAtPhase: currentPhase, stoppedAtCheck: currentCheck, command: currentCommand, failureType, failedObservationWritten,
      apiRestored: restoredApis === 4, cleanupErrors,
      commandTimeoutMilliseconds: 20_000, productionFetchTimeoutVerified: false,
      queryHistoryObservation: historyObservation.snapshot(), historyCommandAttempts,
      transport, promptDriver: true, generatedPayloadOnly: true, finishedAtUtc: new Date().toISOString() });
  }
  assert.equal(outcome, 'PASS', 'WB57 public API cleanup must succeed.');

  async function command(name: string): Promise<void> {
    const previousCheck = currentCheck;
    currentCheck = 'command';
    assert.ok(Date.now() < deadline);
    assert.ok(['sonnetdb.addConnection', 'sonnetdb.selectDatabase', 'sonnetdb.runQuery', 'sonnetdb.runSelection',
      'sonnetdb.explainQuery', 'sonnetdb.showQueryHistory'].includes(name));
    currentCommand = name;
    if (name === 'sonnetdb.showQueryHistory') {
      assert.ok(historyCommandAttempts < 20);
      historyCommandAttempts += 1;
    }
    await bounded(vscode.commands.executeCommand(name), Math.min(20_000, deadline - Date.now()));
    currentCheck = previousCheck;
  }

  async function observePhaseHistory(phase: Phase): Promise<void> {
    const previousCommand = currentCommand;
    const previousCheck = currentCheck;
    const before = historyPicks;
    observedHistory = [];
    let items: unknown;
    try {
      if (historyCommandAttempts < 20 && historyPicks < 20 && Date.now() < deadline) {
        await command('sonnetdb.showQueryHistory');
        if (historyPicks === before + 1) items = observedHistory;
      }
    } catch { /* 观察失败保持 unknown，不替换 query 的主检查或原失败。 */ }
    finally {
      currentCommand = previousCommand;
      currentCheck = previousCheck;
      historyObservation.observeHistory(items, phase.sql.split(/\r?\n/u)[0].slice(0, 100), `${reference.label} / ${reference.database}`);
    }
  }

  function safePayload(value: unknown, phase: Phase): unknown {
    assert.ok(Date.now() < deadline);
    const allowedStrings = new Set<string>(['SonnetDB Query Result', 'Query', phase.sql, reference.label, reference.database,
      'WB57:first', 'WB57:second', 'WB57:third', 'WB57:fourth', 'WB57:fifth']);
    for (const column of phase.result.columns.slice(0, 16)) allowedStrings.add(column);
    for (const row of phase.result.rows.slice(0, 100)) {
      assert.ok(Date.now() < deadline);
      for (const cell of row.slice(0, 16)) if (typeof cell === 'string' && cell.length <= 4096) allowedStrings.add(cell);
    }
    const scalar = (item: unknown): unknown => {
      if (item === null || typeof item === 'boolean' || (typeof item === 'number' && Number.isFinite(item))) return item;
      if (typeof item === 'string') return allowedStrings.has(item) ? item : { omitted: 'unapproved-string', length: item.length };
      return { omitted: 'unsupported-value', kind: typeof item };
    };
    const object = (item: unknown): Record<string, unknown> => item && typeof item === 'object' && !Array.isArray(item)
      ? item as Record<string, unknown> : {};
    const root = object(value);
    const result = object(root.result);
    const source = object(root.source);
    const context = object(root.context);
    const end = object(result.end);
    const columns = Array.isArray(result.columns) ? result.columns : [];
    const rows = Array.isArray(result.rows) ? result.rows : [];
    return { title: scalar(root.title), source: { label: scalar(source.label), text: scalar(source.text) },
      context: { connectionLabel: scalar(context.connectionLabel), database: scalar(context.database) },
      result: { columnsPresent: Array.isArray(result.columns), columnCount: columns.length, columns: columns.slice(0, 16).map(scalar),
        rowsPresent: Array.isArray(result.rows), rowCount: rows.length, rows: rows.slice(0, 100).map((row: unknown) => Array.isArray(row)
          ? { cellCount: row.length, cells: row.slice(0, 16).map(scalar) } : { omitted: 'not-array' }),
        hasColumns: scalar(result.hasColumns), errorPresent: result.error !== null, endPresent: result.end !== null && typeof result.end === 'object',
        end: { type: end.type === 'end' ? 'end' : { omitted: 'unexpected-type' }, rowCount: scalar(end.rowCount),
          recordsAffected: scalar(end.recordsAffected), elapsedMilliseconds: scalar(end.elapsedMilliseconds) } },
      projection: { maximumRows: 100, maximumColumns: 16, stringsFromFrozenReferenceOnly: true, unknownTextOmitted: true } };
  }

  function replace(target: object, property: string, value: unknown): void {
    const descriptor = Object.getOwnPropertyDescriptor(target, property);
    assert.ok(descriptor && (descriptor.configurable || descriptor.writable), `Public VS Code API ${property} must support a reversible driver.`);
    if (descriptor.configurable) Object.defineProperty(target, property, { configurable: true, enumerable: descriptor.enumerable, writable: true, value });
    else Reflect.set(target, property, value);
    restores.push(() => { Object.defineProperty(target, property, descriptor); });
  }

  async function evidence(name: string, value: unknown): Promise<void> {
    assert.equal(evidenceRootVerified, true);
    assert.ok(/^(phase-[1-3]|observation-[1-3]|failure-observation|history|host-result)\.json$/u.test(name));
    const text = `${JSON.stringify(value, null, 2)}\n`;
    for (const secret of secrets) assert.equal(text.includes(secret), false, 'A runtime secret must never enter evidence.');
    const size = Buffer.byteLength(text);
    files += 1; bytes += size;
    assert.ok(files <= 9 && size <= 512 * 1024 && bytes <= 4 * 1024 * 1024);
    await writeFile(path.join(runRoot, name), text, { flag: 'wx' });
  }
}

function required(name: string): string {
  const value = process.env[name]?.trim();
  assert.ok(value, `Required isolated test configuration is missing: ${name}.`);
  return value;
}

function freshProgress(): ObservationProgress {
  return { commandCompleted: false, panelPresent: false, htmlBytes: null, encodedPresent: false, encodedBytes: null, decoded: false };
}

async function bounded<T>(promise: Thenable<T>, milliseconds: number): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([Promise.resolve(promise), new Promise<never>((_resolve, reject) => {
      timer = setTimeout(() => reject(new Error('WB57 public command deadline exceeded.')), milliseconds);
    })]);
  } finally { if (timer) clearTimeout(timer); }
}
