import * as vscode from 'vscode';
import { inferTrajectory } from '../core/geoResult';
import { SqlResultSet } from '../core/types';

interface QueryHistoryEntry {
  id: string;
  timestamp: number;
  sql: string;
  connectionLabel?: string;
  database?: string;
  rowCount: number;
  elapsedMs?: number;
  failed: boolean;
}

interface ResultPayload {
  title: string;
  result: SqlResultSet;
  source: { label: string; text: string };
  context?: { connectionLabel?: string; database?: string };
  raw: unknown;
}

const QueryHistoryStorageKey = 'sonnetdb.queryHistory';

export class QueryResultPanel {
  private panel: vscode.WebviewPanel | undefined;
  private payload: ResultPayload | undefined;

  public constructor(private readonly context: vscode.ExtensionContext) {
    context.subscriptions.push(vscode.commands.registerCommand('sonnetdb.showQueryHistory', () => this.showHistory()));
  }

  public show(
    result: SqlResultSet,
    sql: string,
    connectionLabel?: string,
    database?: string,
  ): void {
    this.showResult('SonnetDB Query Result', result, {
      label: 'Query',
      text: sql,
    }, { connectionLabel, database });
  }

  public showRows(title: string, columns: string[], rows: unknown[][], raw?: unknown, sourceText?: string): void {
    this.showResult(title, {
      columns,
      rows,
      end: null,
      error: null,
      hasColumns: columns.length > 0,
    }, {
      label: 'Source',
      text: sourceText ?? title,
    }, undefined, raw);
  }

  private showResult(
    title: string,
    result: SqlResultSet,
    source: { label: string; text: string },
    context?: { connectionLabel?: string; database?: string },
    raw?: unknown,
  ): void {
    this.payload = {
      title,
      result,
      source,
      context,
      raw: raw ?? result,
    };
    if (!this.panel) {
      this.panel = vscode.window.createWebviewPanel(
        'sonnetdb.queryResult',
        title,
        vscode.ViewColumn.Beside,
        { enableScripts: true, retainContextWhenHidden: true },
      );
      this.panel.onDidDispose(() => {
        this.panel = undefined;
      });
      this.panel.webview.onDidReceiveMessage((message: { type?: string; format?: string }) => {
        if (message.type === 'export' && (message.format === 'csv' || message.format === 'json')) {
          void this.exportResult(message.format);
        }
      });
    } else {
      this.panel.reveal(vscode.ViewColumn.Beside, true);
    }
    this.panel.title = title;
    this.panel.webview.html = this.renderHtml(title, result, source, context, raw);
    if (source.label === 'Query') {
      void this.recordHistory(source.text, result, context);
    }
  }

  private async recordHistory(
    sql: string,
    result: SqlResultSet,
    viewContext?: { connectionLabel?: string; database?: string },
  ): Promise<void> {
    const history = this.context.globalState.get<QueryHistoryEntry[]>(QueryHistoryStorageKey, []);
    history.unshift({
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      timestamp: Date.now(),
      sql,
      connectionLabel: viewContext?.connectionLabel,
      database: viewContext?.database,
      rowCount: result.end?.rowCount ?? result.rows.length,
      elapsedMs: result.end?.elapsedMs,
      failed: Boolean(result.error),
    });
    await this.context.globalState.update(QueryHistoryStorageKey, history.slice(0, 50));
  }

  private async showHistory(): Promise<void> {
    const history = this.context.globalState.get<QueryHistoryEntry[]>(QueryHistoryStorageKey, []);
    if (history.length === 0) {
      void vscode.window.showInformationMessage('SonnetDB query history is empty.');
      return;
    }
    const selected = await vscode.window.showQuickPick(history.map((entry) => ({
      label: entry.sql.split(/\r?\n/u)[0].slice(0, 100),
      description: `${entry.failed ? 'failed' : `${entry.rowCount} rows`} · ${new Date(entry.timestamp).toLocaleString()}`,
      detail: [entry.connectionLabel, entry.database, entry.elapsedMs === undefined ? undefined : `${entry.elapsedMs} ms`]
        .filter(Boolean)
        .join(' / '),
      entry,
    })), { placeHolder: 'Restore a SonnetDB query from local history' });
    if (!selected) {
      return;
    }
    const document = await vscode.workspace.openTextDocument({ language: 'sql', content: selected.entry.sql });
    await vscode.window.showTextDocument(document);
  }

  private async exportResult(format: 'csv' | 'json'): Promise<void> {
    if (!this.payload) {
      return;
    }
    const uri = await vscode.window.showSaveDialog({
      saveLabel: `Export ${format.toUpperCase()}`,
      filters: format === 'csv' ? { CSV: ['csv'] } : { JSON: ['json'] },
      defaultUri: vscode.Uri.file(`sonnetdb-result.${format}`),
    });
    if (!uri) {
      return;
    }
    const text = format === 'csv'
      ? toCsv(this.payload.result.columns, this.payload.result.rows)
      : JSON.stringify(this.payload.raw, null, 2);
    await vscode.workspace.fs.writeFile(uri, Buffer.from(text, 'utf8'));
    void vscode.window.showInformationMessage(`Exported SonnetDB result to ${uri.fsPath}.`);
  }

  private renderHtml(
    title: string,
    result: SqlResultSet,
    source: { label: string; text: string },
    viewContext?: { connectionLabel?: string; database?: string },
    raw?: unknown,
  ): string {
    const nonce = createNonce();
    const payload = Buffer.from(JSON.stringify({
      result,
      raw: raw ?? result,
      source,
      context: viewContext,
      title,
      trajectory: inferTrajectory(result.columns, result.rows),
    }), 'utf8').toString('base64');

    return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>${escapeHtml(title)}</title>
    <style>
      :root {
        color-scheme: light dark;
      }
      body {
        margin: 0;
        font-family: var(--vscode-font-family);
        color: var(--vscode-foreground);
        background: var(--vscode-editor-background);
      }
      .shell {
        padding: 14px;
      }
      .source {
        margin-bottom: 12px;
        border: 1px solid var(--vscode-panel-border);
        background: var(--vscode-sideBar-background);
      }
      .source h2 {
        margin: 0;
        padding: 8px 10px;
        font-size: 12px;
        font-weight: 600;
        border-bottom: 1px solid var(--vscode-panel-border);
      }
      pre {
        margin: 0;
        padding: 10px;
        overflow: auto;
        white-space: pre-wrap;
        word-break: break-word;
        font-family: var(--vscode-editor-font-family);
        font-size: var(--vscode-editor-font-size);
      }
      .tabs {
        display: flex;
        align-items: center;
        flex-wrap: wrap;
        gap: 4px;
        border-bottom: 1px solid var(--vscode-panel-border);
      }
      .tabs .summary {
        margin-left: auto;
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
        white-space: nowrap;
      }
      .tabs .export {
        border: 1px solid var(--vscode-button-border, transparent);
        padding: 4px 8px;
        margin-left: 4px;
      }
      button {
        appearance: none;
        border: 0;
        border-bottom: 2px solid transparent;
        padding: 8px 12px;
        color: var(--vscode-foreground);
        background: transparent;
        cursor: pointer;
      }
      button[aria-selected="true"] {
        border-bottom-color: var(--vscode-focusBorder);
        color: var(--vscode-tab-activeForeground);
      }
      .pane {
        display: none;
        padding-top: 12px;
      }
      .pane.active {
        display: block;
      }
      .table-wrap {
        max-height: calc(100vh - 180px);
        overflow: auto;
        border: 1px solid var(--vscode-panel-border);
      }
      table {
        width: 100%;
        border-collapse: collapse;
        font-size: 12px;
      }
      th,
      td {
        padding: 6px 8px;
        border-bottom: 1px solid var(--vscode-panel-border);
        vertical-align: top;
        text-align: left;
      }
      th {
        position: sticky;
        top: 0;
        background: var(--vscode-sideBar-background);
        z-index: 1;
      }
      td {
        font-family: var(--vscode-editor-font-family);
        max-width: 420px;
        overflow-wrap: anywhere;
      }
      .notice {
        color: var(--vscode-descriptionForeground);
        padding: 12px;
      }
      canvas {
        width: 100%;
        height: min(420px, calc(100vh - 210px));
        box-sizing: border-box;
        border: 1px solid var(--vscode-panel-border);
        background: var(--vscode-editor-background);
      }
      #trajectory canvas {
        height: min(500px, calc(100vh - 240px));
      }
      .trajectory-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 8px 16px;
        margin-bottom: 8px;
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }
      .trajectory-meta strong {
        color: var(--vscode-foreground);
        font-weight: 600;
      }
      .error {
        margin: 0 0 12px;
        padding: 10px;
        color: var(--vscode-errorForeground);
        border: 1px solid var(--vscode-inputValidation-errorBorder);
        background: var(--vscode-inputValidation-errorBackground);
      }
    </style>
  </head>
  <body>
    <div class="shell">
      <div class="source">
        <h2 id="source-title"></h2>
        <pre id="source-text"></pre>
      </div>
      <div id="error" class="error" hidden></div>
      <div class="tabs" role="tablist">
        <button data-tab="table" role="tab" aria-selected="true">Table</button>
        <button data-tab="raw" role="tab" aria-selected="false">Raw</button>
        <button data-tab="chart" role="tab" aria-selected="false">Chart</button>
        <button data-tab="trajectory" role="tab" aria-selected="false" hidden>Trajectory</button>
        <span id="summary" class="summary"></span>
        <button class="export" data-export="csv" title="Export result as CSV">Export CSV</button>
        <button class="export" data-export="json" title="Export result as JSON">Export JSON</button>
      </div>
      <section id="table" class="pane active" role="tabpanel"></section>
      <section id="raw" class="pane" role="tabpanel"></section>
      <section id="chart" class="pane" role="tabpanel"></section>
      <section id="trajectory" class="pane" role="tabpanel"></section>
    </div>
    <script type="application/json" id="payload" nonce="${nonce}">${payload}</script>
    <script nonce="${nonce}">
      const encodedPayload = document.getElementById('payload').textContent.trim();
      const payloadBytes = Uint8Array.from(atob(encodedPayload), (character) => character.charCodeAt(0));
      const data = JSON.parse(new TextDecoder().decode(payloadBytes));
      const vscode = acquireVsCodeApi();
      const result = data.result;
      const rows = Array.isArray(result.rows) ? result.rows : [];
      const columns = Array.isArray(result.columns) ? result.columns : [];

      document.getElementById('source-title').textContent = data.source.label;
      document.getElementById('source-text').textContent = data.source.text || '';
      const rowCount = result.end && Number.isFinite(result.end.rowCount) ? result.end.rowCount : rows.length;
      const elapsed = result.end && Number.isFinite(result.end.elapsedMs) ? ' · ' + result.end.elapsedMs + ' ms' : '';
      const target = data.context && [data.context.connectionLabel, data.context.database].filter(Boolean).join(' / ');
      document.getElementById('summary').textContent = rowCount + ' rows' + elapsed + (target ? ' · ' + target : '');

      if (result.error && result.error.message) {
        const error = document.getElementById('error');
        error.hidden = false;
        error.textContent = result.error.code ? result.error.code + ': ' + result.error.message : result.error.message;
      }

      for (const tab of document.querySelectorAll('[data-tab]')) {
        tab.addEventListener('click', () => activateTab(tab.dataset.tab));
      }
      for (const button of document.querySelectorAll('[data-export]')) {
        button.addEventListener('click', () => vscode.postMessage({ type: 'export', format: button.dataset.export }));
      }

      renderTable();
      renderRaw();
      renderChart();
      renderTrajectory();

      function activateTab(name) {
        for (const tab of document.querySelectorAll('[data-tab]')) {
          tab.setAttribute('aria-selected', String(tab.dataset.tab === name));
        }
        for (const pane of document.querySelectorAll('.pane')) {
          pane.classList.toggle('active', pane.id === name);
        }
        if (name === 'chart') {
          drawChart();
        }
        if (name === 'trajectory') {
          drawTrajectory();
        }
      }

      function renderTable() {
        const target = document.getElementById('table');
        if (!columns.length) {
          target.innerHTML = '<div class="notice">No tabular columns returned.</div>';
          return;
        }

        const table = document.createElement('table');
        const thead = document.createElement('thead');
        const headRow = document.createElement('tr');
        for (const column of columns) {
          const th = document.createElement('th');
          th.textContent = String(column);
          headRow.appendChild(th);
        }
        thead.appendChild(headRow);
        table.appendChild(thead);

        const tbody = document.createElement('tbody');
        for (const row of rows) {
          const tr = document.createElement('tr');
          for (let i = 0; i < columns.length; i += 1) {
            const td = document.createElement('td');
            td.textContent = formatCell(Array.isArray(row) ? row[i] : undefined);
            tr.appendChild(td);
          }
          tbody.appendChild(tr);
        }
        table.appendChild(tbody);

        const wrap = document.createElement('div');
        wrap.className = 'table-wrap';
        wrap.appendChild(table);
        target.replaceChildren(wrap);
      }

      function renderRaw() {
        const pre = document.createElement('pre');
        pre.textContent = JSON.stringify(data.raw, null, 2);
        document.getElementById('raw').replaceChildren(pre);
      }

      function renderChart() {
        const target = document.getElementById('chart');
        if (!columns.length || !rows.length) {
          target.innerHTML = '<div class="notice">No rows available for charting.</div>';
          return;
        }

        const chart = inferChart();
        if (!chart) {
          target.innerHTML = '<div class="notice">No numeric series found for charting.</div>';
          return;
        }

        const canvas = document.createElement('canvas');
        canvas.width = 1200;
        canvas.height = 420;
        canvas.dataset.chart = JSON.stringify(chart);
        target.replaceChildren(canvas);
        drawChart();
      }

      function drawChart() {
        const canvas = document.querySelector('#chart canvas');
        if (!canvas) {
          return;
        }
        const chart = JSON.parse(canvas.dataset.chart);
        const ctx = canvas.getContext('2d');
        const width = canvas.width;
        const height = canvas.height;
        const pad = 44;
        ctx.clearRect(0, 0, width, height);
        ctx.strokeStyle = getCss('--vscode-panel-border') || '#999';
        ctx.fillStyle = getCss('--vscode-descriptionForeground') || '#777';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(pad, pad);
        ctx.lineTo(pad, height - pad);
        ctx.lineTo(width - pad, height - pad);
        ctx.stroke();

        const values = chart.series.flatMap((series) => series.values);
        const min = Math.min(...values);
        const max = Math.max(...values);
        const span = max === min ? 1 : max - min;
        const colors = [
          getCss('--vscode-charts-blue') || '#3794ff',
          getCss('--vscode-charts-green') || '#89d185',
          getCss('--vscode-charts-orange') || '#d18616',
          getCss('--vscode-charts-purple') || '#b180d7',
        ];
        chart.series.forEach((series, seriesIndex) => {
          const xStep = series.values.length <= 1 ? 0 : (width - pad * 2) / (series.values.length - 1);
          ctx.strokeStyle = colors[seriesIndex % colors.length];
          ctx.lineWidth = 2;
          ctx.beginPath();
          series.values.forEach((value, index) => {
            const x = pad + xStep * index;
            const y = height - pad - ((value - min) / span) * (height - pad * 2);
            if (index === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          });
          ctx.stroke();
        });

        ctx.fillStyle = getCss('--vscode-foreground') || '#ddd';
        ctx.font = '12px ' + getComputedStyle(document.body).fontFamily;
        ctx.fillText(chart.series.map((series) => series.column).join(' · ') + ' (' + min.toPrecision(4) + ' - ' + max.toPrecision(4) + ')', pad, 24);
        ctx.fillText(chart.xColumn, width - pad - 120, height - 12);
      }

      function inferChart() {
        const x = columns
          .map((column, index) => ({ column, index }))
          .find((entry) => /(^time$|timestamp|_at$|utc$)/iu.test(String(entry.column)))
          ?? { column: columns[0] || 'row', index: columns.length ? 0 : -1 };
        const numericColumns = columns
          .map((column, index) => ({ column, index }))
          .filter((entry) => entry.index !== x.index && rows.some((row) =>
            Number.isFinite(Number(Array.isArray(row) ? row[entry.index] : undefined))))
          .slice(0, 4);
        if (!numericColumns.length) {
          return null;
        }

        const series = numericColumns.map((entry) => ({
          column: String(entry.column),
          values: rows
            .map((row) => Number(Array.isArray(row) ? row[entry.index] : undefined))
            .filter((value) => Number.isFinite(value)),
        })).filter((entry) => entry.values.length > 0);
        if (!series.length) {
          return null;
        }

        return {
          xColumn: String(x.column),
          series,
        };
      }

      function renderTrajectory() {
        const target = document.getElementById('trajectory');
        const trajectory = data.trajectory;
        if (!trajectory || !Array.isArray(trajectory.series) || !trajectory.series.length) {
          target.innerHTML = '<div class="notice">No GEOPOINT column found for trajectory rendering.</div>';
          return;
        }

        document.querySelector('[data-tab="trajectory"]').hidden = false;
        const meta = document.createElement('div');
        meta.className = 'trajectory-meta';
        const fields = [
          ['GEOPOINT', trajectory.geoColumn],
          ['Points', String(trajectory.pointCount)],
          ['Tracks', String(trajectory.series.length)],
          ['Order', trajectory.timeColumn || 'result order'],
          ['Group', trajectory.groupColumn || 'single track'],
        ];
        for (const field of fields) {
          const item = document.createElement('span');
          const label = document.createElement('strong');
          label.textContent = field[0] + ': ';
          item.append(label, document.createTextNode(field[1]));
          meta.appendChild(item);
        }

        const canvas = document.createElement('canvas');
        canvas.width = 1200;
        canvas.height = 500;
        canvas.setAttribute('role', 'img');
        canvas.setAttribute('aria-label', trajectory.pointCount + ' geographic points in ' + trajectory.series.length + ' trajectories');
        target.replaceChildren(meta, canvas);
      }

      function drawTrajectory() {
        const canvas = document.querySelector('#trajectory canvas');
        const trajectory = data.trajectory;
        if (!canvas || !trajectory) {
          return;
        }

        const ctx = canvas.getContext('2d');
        const width = canvas.width;
        const height = canvas.height;
        const pad = { left: 72, right: 28, top: 54, bottom: 54 };
        const plotWidth = width - pad.left - pad.right;
        const plotHeight = height - pad.top - pad.bottom;
        const bounds = trajectory.bounds;
        const lonSpan = bounds.maxLon - bounds.minLon || 0.02;
        const latSpan = bounds.maxLat - bounds.minLat || 0.02;
        const minLon = bounds.minLon - lonSpan * 0.06;
        const maxLon = bounds.maxLon + lonSpan * 0.06;
        const minLat = bounds.minLat - latSpan * 0.06;
        const maxLat = bounds.maxLat + latSpan * 0.06;
        const midLat = (minLat + maxLat) / 2;
        const lonFactor = Math.max(0.15, Math.cos(midLat * Math.PI / 180));
        const projectedWidth = (maxLon - minLon) * lonFactor;
        const projectedHeight = maxLat - minLat;
        const scale = Math.min(plotWidth / projectedWidth, plotHeight / projectedHeight);
        const usedWidth = projectedWidth * scale;
        const usedHeight = projectedHeight * scale;
        const offsetX = pad.left + (plotWidth - usedWidth) / 2;
        const offsetY = pad.top + (plotHeight - usedHeight) / 2;
        const mapX = (lon) => offsetX + (lon - minLon) * lonFactor * scale;
        const mapY = (lat) => offsetY + (maxLat - lat) * scale;

        ctx.clearRect(0, 0, width, height);
        ctx.fillStyle = getCss('--vscode-editor-background') || '#1e1e1e';
        ctx.fillRect(0, 0, width, height);
        ctx.strokeStyle = getCss('--vscode-panel-border') || '#555';
        ctx.fillStyle = getCss('--vscode-descriptionForeground') || '#999';
        ctx.font = '11px ' + getComputedStyle(document.body).fontFamily;
        ctx.lineWidth = 1;

        for (let step = 0; step <= 4; step += 1) {
          const lon = minLon + ((maxLon - minLon) * step) / 4;
          const lat = minLat + ((maxLat - minLat) * step) / 4;
          const x = mapX(lon);
          const y = mapY(lat);
          ctx.globalAlpha = 0.55;
          ctx.beginPath();
          ctx.moveTo(x, offsetY);
          ctx.lineTo(x, offsetY + usedHeight);
          ctx.moveTo(offsetX, y);
          ctx.lineTo(offsetX + usedWidth, y);
          ctx.stroke();
          ctx.globalAlpha = 1;
          ctx.fillText(lon.toFixed(5), x - 28, offsetY + usedHeight + 22);
          ctx.fillText(lat.toFixed(5), 8, y + 4);
        }

        const colors = [
          getCss('--vscode-charts-blue') || '#3794ff',
          getCss('--vscode-charts-green') || '#89d185',
          getCss('--vscode-charts-orange') || '#d18616',
          getCss('--vscode-charts-purple') || '#b180d7',
          getCss('--vscode-charts-red') || '#f14c4c',
          getCss('--vscode-charts-yellow') || '#cca700',
        ];

        trajectory.series.forEach((series, seriesIndex) => {
          const color = colors[seriesIndex % colors.length];
          ctx.strokeStyle = color;
          ctx.fillStyle = color;
          ctx.lineWidth = 2.5;
          ctx.beginPath();
          series.points.forEach((point, pointIndex) => {
            const x = mapX(point.lon);
            const y = mapY(point.lat);
            if (pointIndex === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
          });
          ctx.stroke();

          if (series.points.length <= 200) {
            for (const point of series.points) {
              ctx.beginPath();
              ctx.arc(mapX(point.lon), mapY(point.lat), 3, 0, Math.PI * 2);
              ctx.fill();
            }
          }

          const start = series.points[0];
          const end = series.points[series.points.length - 1];
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.arc(mapX(start.lon), mapY(start.lat), 7, 0, Math.PI * 2);
          ctx.stroke();
          ctx.fillRect(mapX(end.lon) - 6, mapY(end.lat) - 6, 12, 12);
        });

        let legendX = pad.left;
        let renderedLegendCount = 0;
        ctx.font = '12px ' + getComputedStyle(document.body).fontFamily;
        for (let seriesIndex = 0; seriesIndex < trajectory.series.length; seriesIndex += 1) {
          const series = trajectory.series[seriesIndex];
          const color = colors[seriesIndex % colors.length];
          const rawLabel = series.name + ' (' + series.points.length + ')';
          const label = rawLabel.length > 24 ? rawLabel.slice(0, 21) + '...' : rawLabel;
          const itemWidth = Math.min(190, ctx.measureText(label).width + 42);
          if (legendX + itemWidth > width - pad.right) {
            break;
          }
          ctx.fillStyle = color;
          ctx.fillRect(legendX, 20, 14, 4);
          ctx.fillStyle = getCss('--vscode-foreground') || '#ddd';
          ctx.fillText(label, legendX + 20, 26);
          legendX += itemWidth;
          renderedLegendCount += 1;
        }
        if (renderedLegendCount < trajectory.series.length) {
          ctx.fillStyle = getCss('--vscode-descriptionForeground') || '#999';
          ctx.fillText('+' + (trajectory.series.length - renderedLegendCount) + ' tracks', legendX + 4, 26);
        }

        ctx.fillStyle = getCss('--vscode-descriptionForeground') || '#999';
        ctx.fillText('Longitude', offsetX + usedWidth - 58, height - 12);
        ctx.save();
        ctx.translate(15, offsetY + 48);
        ctx.rotate(-Math.PI / 2);
        ctx.fillText('Latitude', 0, 0);
        ctx.restore();
      }

      function formatCell(value) {
        if (value === null || value === undefined) {
          return '';
        }
        if (typeof value === 'object') {
          return JSON.stringify(value);
        }
        return String(value);
      }

      function getCss(name) {
        return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
      }
    </script>
  </body>
</html>`;
  }
}

function createNonce(): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let value = '';
  for (let i = 0; i < 32; i += 1) {
    value += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return value;
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/gu, '&amp;')
    .replace(/</gu, '&lt;')
    .replace(/>/gu, '&gt;')
    .replace(/"/gu, '&quot;');
}

function toCsv(columns: string[], rows: unknown[][]): string {
  return [columns, ...rows]
    .map((row) => row.map(csvCell).join(','))
    .join('\r\n');
}

function csvCell(value: unknown): string {
  const text = value === null || value === undefined
    ? ''
    : typeof value === 'object'
      ? JSON.stringify(value)
      : String(value);
  return /[",\r\n]/u.test(text) ? `"${text.replace(/"/gu, '""')}"` : text;
}
