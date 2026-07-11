import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import {
  DocumentFilter,
  DocumentFindRequest,
  DocumentFindResponse,
  DocumentProjection,
  DocumentSort,
  SonnetDbConnectionProfile,
} from '../core/types';

interface DocumentTarget {
  profile: SonnetDbConnectionProfile;
  database: string;
  collection: string;
}

interface RunMessage {
  type: 'run';
  filterText?: string;
  projectionText?: string;
  sortText?: string;
  limit?: number;
}

interface ActionMessage {
  type: 'next' | 'previous' | 'export';
}

type DocumentPanelMessage = RunMessage | ActionMessage;

export class DocumentQueryPanel implements vscode.Disposable {
  private panel: vscode.WebviewPanel | undefined;
  private target: DocumentTarget | undefined;
  private client: SonnetDbClient | undefined;
  private baseRequest: DocumentFindRequest = { limit: 50 };
  private response: DocumentFindResponse | undefined;
  private cursors: Array<string | undefined> = [undefined];
  private cursorIndex = 0;
  private requestVersion = 0;

  public constructor(
    private readonly createClient: (profile: SonnetDbConnectionProfile) => Promise<SonnetDbClient>,
  ) {}

  public async show(target: DocumentTarget): Promise<void> {
    const changedTarget = !this.target
      || this.target.profile.id !== target.profile.id
      || this.target.database !== target.database
      || this.target.collection !== target.collection;

    this.target = target;
    this.client = await this.createClient(target.profile);
    if (changedTarget) {
      this.baseRequest = { limit: 50 };
      this.response = undefined;
      this.cursors = [undefined];
      this.cursorIndex = 0;
    }

    let shouldRender = changedTarget;
    if (!this.panel) {
      shouldRender = true;
      this.panel = vscode.window.createWebviewPanel(
        'sonnetdb.documentQuery',
        `Document: ${target.collection}`,
        vscode.ViewColumn.Beside,
        { enableScripts: true, retainContextWhenHidden: true },
      );
      this.panel.onDidDispose(() => {
        this.panel = undefined;
      });
      this.panel.webview.onDidReceiveMessage((message: DocumentPanelMessage) => {
        void this.handleMessage(message);
      });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }

    this.panel.title = `Document: ${target.collection}`;
    if (!shouldRender) {
      return;
    }
    this.panel.webview.html = renderHtml(target);
    await this.execute();
  }

  public dispose(): void {
    this.panel?.dispose();
  }

  private async handleMessage(message: DocumentPanelMessage): Promise<void> {
    try {
      if (message.type === 'run') {
        this.baseRequest = buildRequest(message);
        this.cursors = [undefined];
        this.cursorIndex = 0;
        await this.execute();
        return;
      }
      if (message.type === 'next') {
        const token = this.response?.continuationToken ?? undefined;
        if (!token || !this.response?.hasMore) {
          return;
        }
        this.cursors = this.cursors.slice(0, this.cursorIndex + 1);
        this.cursors.push(token);
        this.cursorIndex += 1;
        await this.execute();
        return;
      }
      if (message.type === 'previous') {
        if (this.cursorIndex === 0) {
          return;
        }
        this.cursorIndex -= 1;
        await this.execute();
        return;
      }
      if (message.type === 'export') {
        await this.exportResult();
      }
    } catch (error) {
      await this.post({ type: 'error', message: errorMessage(error) });
    }
  }

  private async execute(): Promise<void> {
    if (!this.target || !this.client) {
      return;
    }

    const version = ++this.requestVersion;
    await this.post({ type: 'loading' });
    try {
      const request: DocumentFindRequest = {
        ...this.baseRequest,
        continuationToken: this.cursors[this.cursorIndex],
      };
      const response = await this.client.findDocuments(
        this.target.database,
        this.target.collection,
        request,
      );
      if (version !== this.requestVersion) {
        return;
      }
      this.response = response;
      await this.post({
        type: 'result',
        response,
        request,
        page: this.cursorIndex + 1,
        canPrevious: this.cursorIndex > 0,
        canNext: response.hasMore && Boolean(response.continuationToken),
      });
    } catch (error) {
      if (version === this.requestVersion) {
        await this.post({ type: 'error', message: errorMessage(error) });
      }
    }
  }

  private async exportResult(): Promise<void> {
    if (!this.response || !this.target) {
      return;
    }
    const uri = await vscode.window.showSaveDialog({
      saveLabel: 'Export Document Page',
      filters: { JSON: ['json'], JSONL: ['jsonl'] },
      defaultUri: vscode.Uri.file(`${this.target.collection}-page-${this.cursorIndex + 1}.json`),
    });
    if (!uri) {
      return;
    }

    const isJsonLines = uri.path.toLowerCase().endsWith('.jsonl');
    const text = isJsonLines
      ? this.response.documents.map((item) => JSON.stringify({ _id: item.id, ...asObject(item.document) })).join('\n')
      : JSON.stringify(this.response, null, 2);
    await vscode.workspace.fs.writeFile(uri, Buffer.from(text, 'utf8'));
    void vscode.window.showInformationMessage(`Exported Document results to ${uri.fsPath}.`);
  }

  private async post(message: unknown): Promise<void> {
    await this.panel?.webview.postMessage(message);
  }
}

function buildRequest(message: RunMessage): DocumentFindRequest {
  const limit = Number(message.limit ?? 50);
  if (!Number.isInteger(limit) || limit < 1 || limit > 1000) {
    throw new Error('Limit must be an integer between 1 and 1000.');
  }

  const filter = parseOptionalJson<DocumentFilter>(message.filterText, 'Filter', 'object');
  const projection = parseOptionalJson<DocumentProjection[]>(message.projectionText, 'Projection', 'array');
  const sort = parseOptionalJson<DocumentSort[]>(message.sortText, 'Sort', 'array');

  if (projection?.some((item) => !isRecord(item) || (item.path !== undefined && typeof item.path !== 'string'))) {
    throw new Error('Projection entries must be objects with an optional string path.');
  }
  if (sort?.some((item) => !isRecord(item) || typeof item.path !== 'string')) {
    throw new Error('Sort entries must contain a string path.');
  }

  return {
    filter,
    projection,
    sort,
    limit,
  };
}

function parseOptionalJson<T>(
  text: string | undefined,
  label: string,
  shape: 'object' | 'array',
): T | undefined {
  const value = text?.trim();
  if (!value) {
    return undefined;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(value);
  } catch (error) {
    throw new Error(`${label} is not valid JSON: ${errorMessage(error)}`);
  }
  if ((shape === 'array' && !Array.isArray(parsed)) || (shape === 'object' && !isRecord(parsed))) {
    throw new Error(`${label} must be a JSON ${shape}.`);
  }
  return parsed as T;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

function asObject(value: unknown): Record<string, unknown> {
  return isRecord(value) ? value : { document: value };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function renderHtml(target: DocumentTarget): string {
  const nonce = createNonce();
  const context = Buffer.from(JSON.stringify({
    connection: target.profile.label,
    database: target.database,
    collection: target.collection,
  }), 'utf8').toString('base64');

  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Document Query</title>
    <style>
      :root { color-scheme: light dark; }
      * { box-sizing: border-box; }
      body { margin: 0; color: var(--vscode-foreground); background: var(--vscode-editor-background); font-family: var(--vscode-font-family); }
      button, input, textarea { font: inherit; }
      .shell { min-height: 100vh; display: grid; grid-template-rows: auto auto 1fr; }
      header { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 12px 16px; border-bottom: 1px solid var(--vscode-panel-border); background: var(--vscode-sideBar-background); }
      .identity { min-width: 0; }
      h1 { margin: 0; font-size: 15px; font-weight: 600; }
      .context { margin-top: 3px; color: var(--vscode-descriptionForeground); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
      .badge { flex: 0 0 auto; color: var(--vscode-testing-iconPassed); font-size: 12px; }
      .query { display: grid; grid-template-columns: minmax(240px, 1fr) minmax(180px, .65fr) minmax(180px, .65fr); gap: 10px; padding: 12px 16px; border-bottom: 1px solid var(--vscode-panel-border); }
      label { display: grid; gap: 5px; min-width: 0; color: var(--vscode-descriptionForeground); font-size: 11px; font-weight: 600; text-transform: uppercase; }
      textarea, input { width: 100%; color: var(--vscode-input-foreground); background: var(--vscode-input-background); border: 1px solid var(--vscode-input-border, var(--vscode-panel-border)); outline: none; }
      textarea { min-height: 82px; padding: 7px 8px; resize: vertical; font-family: var(--vscode-editor-font-family); font-size: 12px; line-height: 1.45; }
      input { height: 28px; padding: 4px 7px; }
      textarea:focus, input:focus { border-color: var(--vscode-focusBorder); }
      .query-actions { grid-column: 1 / -1; display: flex; align-items: center; gap: 8px; }
      .limit { width: 92px; display: flex; align-items: center; gap: 6px; color: var(--vscode-descriptionForeground); font-size: 12px; }
      .limit input { width: 58px; }
      button { min-height: 28px; padding: 4px 10px; border: 1px solid var(--vscode-button-border, transparent); color: var(--vscode-button-foreground); background: var(--vscode-button-background); cursor: pointer; }
      button:hover { background: var(--vscode-button-hoverBackground); }
      button.secondary { color: var(--vscode-foreground); background: transparent; border-color: var(--vscode-panel-border); }
      button.secondary:hover { background: var(--vscode-toolbar-hoverBackground); }
      button:disabled { opacity: .45; cursor: default; }
      .status { margin-left: auto; color: var(--vscode-descriptionForeground); font-size: 12px; }
      .workspace { min-height: 0; display: grid; grid-template-rows: auto 1fr; padding: 0 16px 16px; }
      .result-bar { min-height: 42px; display: flex; align-items: center; gap: 8px; border-bottom: 1px solid var(--vscode-panel-border); }
      .summary { color: var(--vscode-descriptionForeground); font-size: 12px; }
      .result-actions { margin-left: auto; display: flex; gap: 6px; }
      .result-actions button { color: var(--vscode-foreground); background: transparent; border-color: var(--vscode-panel-border); }
      .error { margin: 12px 0 0; padding: 9px 10px; color: var(--vscode-errorForeground); border: 1px solid var(--vscode-inputValidation-errorBorder); background: var(--vscode-inputValidation-errorBackground); }
      .table-wrap { min-height: 0; overflow: auto; border: 1px solid var(--vscode-panel-border); border-top: 0; }
      table { width: 100%; border-collapse: collapse; font-size: 12px; }
      th, td { padding: 7px 9px; border-bottom: 1px solid var(--vscode-panel-border); vertical-align: top; text-align: left; }
      th { position: sticky; top: 0; z-index: 1; background: var(--vscode-sideBar-background); font-weight: 600; }
      td.id, td.version { white-space: nowrap; font-family: var(--vscode-editor-font-family); }
      td.document { width: 100%; font-family: var(--vscode-editor-font-family); white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.45; }
      .empty { padding: 36px 12px; color: var(--vscode-descriptionForeground); text-align: center; }
      @media (max-width: 760px) { .query { grid-template-columns: 1fr; } .query-actions { grid-column: 1; flex-wrap: wrap; } .status { width: 100%; margin-left: 0; } header { align-items: flex-start; } }
    </style>
  </head>
  <body>
    <main class="shell">
      <header>
        <div class="identity"><h1 id="title"></h1><div id="context" class="context"></div></div>
        <span class="badge">Read only</span>
      </header>
      <section class="query" aria-label="Document query">
        <label>Filter<textarea id="filter" spellcheck="false" placeholder='{ "path": "$.status", "op": "eq", "value": "online" }'></textarea></label>
        <label>Projection<textarea id="projection" spellcheck="false" placeholder='[{ "name": "status", "path": "$.status" }]'></textarea></label>
        <label>Sort<textarea id="sort" spellcheck="false" placeholder='[{ "path": "$.updatedAt", "descending": true }]'></textarea></label>
        <div class="query-actions">
          <button id="run" title="Run Document find">Run find</button>
          <button id="reset" class="secondary" title="Clear query fields">Reset</button>
          <span class="limit">Limit <input id="limit" type="number" min="1" max="1000" value="50" /></span>
          <span id="status" class="status">Ready</span>
        </div>
      </section>
      <section class="workspace">
        <div class="result-bar">
          <span id="summary" class="summary">No results loaded</span>
          <div class="result-actions">
            <button id="previous" disabled title="Previous cursor page">Previous</button>
            <button id="next" disabled title="Next cursor page">Next</button>
            <button id="export" disabled title="Export current page as JSON or JSONL">Export</button>
          </div>
        </div>
        <div id="error" class="error" hidden></div>
        <div id="results" class="table-wrap"><div class="empty">Run a query to browse this collection.</div></div>
      </section>
    </main>
    <script type="application/json" id="context-payload" nonce="${nonce}">${context}</script>
    <script nonce="${nonce}">
      const vscode = acquireVsCodeApi();
      const bytes = Uint8Array.from(atob(document.getElementById('context-payload').textContent.trim()), character => character.charCodeAt(0));
      const context = JSON.parse(new TextDecoder().decode(bytes));
      const filter = document.getElementById('filter');
      const projection = document.getElementById('projection');
      const sort = document.getElementById('sort');
      const limit = document.getElementById('limit');
      const run = document.getElementById('run');
      const previous = document.getElementById('previous');
      const next = document.getElementById('next');
      const exportButton = document.getElementById('export');
      const error = document.getElementById('error');
      document.getElementById('title').textContent = context.collection;
      document.getElementById('context').textContent = context.connection + ' / ' + context.database + ' / Documents';
      run.addEventListener('click', () => vscode.postMessage({ type: 'run', filterText: filter.value, projectionText: projection.value, sortText: sort.value, limit: Number(limit.value) }));
      document.getElementById('reset').addEventListener('click', () => { filter.value = ''; projection.value = ''; sort.value = ''; limit.value = '50'; filter.focus(); });
      previous.addEventListener('click', () => vscode.postMessage({ type: 'previous' }));
      next.addEventListener('click', () => vscode.postMessage({ type: 'next' }));
      exportButton.addEventListener('click', () => vscode.postMessage({ type: 'export' }));
      window.addEventListener('message', event => {
        const message = event.data;
        if (message.type === 'loading') {
          setBusy(true);
          error.hidden = true;
          document.getElementById('status').textContent = 'Running...';
          return;
        }
        setBusy(false);
        if (message.type === 'error') {
          error.hidden = false;
          error.textContent = message.message || 'Document query failed.';
          document.getElementById('status').textContent = 'Failed';
          return;
        }
        if (message.type !== 'result') return;
        error.hidden = true;
        previous.disabled = !message.canPrevious;
        next.disabled = !message.canNext;
        exportButton.disabled = false;
        const response = message.response;
        const expires = response.cursorExpiresAtUtc ? ' · cursor ' + new Date(response.cursorExpiresAtUtc).toLocaleTimeString() : '';
        document.getElementById('summary').textContent = response.count + ' documents · page ' + message.page + (response.hasMore ? ' · more available' : ' · end') + expires;
        document.getElementById('status').textContent = 'Loaded ' + response.count;
        renderRows(response.documents || []);
      });
      function setBusy(value) { run.disabled = value; previous.disabled = value || previous.disabled; next.disabled = value || next.disabled; }
      function renderRows(rows) {
        const target = document.getElementById('results');
        if (!rows.length) { target.innerHTML = '<div class="empty">No documents matched this query.</div>'; return; }
        const table = document.createElement('table');
        const head = document.createElement('thead');
        const headRow = document.createElement('tr');
        for (const name of ['ID', 'Version', 'Document']) { const th = document.createElement('th'); th.textContent = name; headRow.appendChild(th); }
        head.appendChild(headRow); table.appendChild(head);
        const body = document.createElement('tbody');
        for (const row of rows) {
          const tr = document.createElement('tr');
          const id = document.createElement('td'); id.className = 'id'; id.textContent = String(row.id ?? '');
          const version = document.createElement('td'); version.className = 'version'; version.textContent = String(row.version ?? '');
          const value = document.createElement('td'); value.className = 'document'; value.textContent = JSON.stringify(row.document, null, 2);
          tr.append(id, version, value); body.appendChild(tr);
        }
        table.appendChild(body); target.replaceChildren(table);
      }
    </script>
  </body>
</html>`;
}

function createNonce(): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let value = '';
  for (let index = 0; index < 32; index += 1) {
    value += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return value;
}
