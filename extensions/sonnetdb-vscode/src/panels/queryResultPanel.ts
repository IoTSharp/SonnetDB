import * as vscode from 'vscode';
import { SqlResultSet } from '../core/types';

export class QueryResultPanel {
  public show(result: SqlResultSet, sql: string): void {
    this.showResult('SonnetDB Query Result', result, {
      label: 'Query',
      text: sql,
    });
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
    }, raw);
  }

  private showResult(
    title: string,
    result: SqlResultSet,
    source: { label: string; text: string },
    raw?: unknown,
  ): void {
    const panel = vscode.window.createWebviewPanel(
      'sonnetdb.queryResult',
      title,
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
      },
    );

    panel.webview.html = this.renderHtml(title, result, source, raw);
  }

  private renderHtml(
    title: string,
    result: SqlResultSet,
    source: { label: string; text: string },
    raw?: unknown,
  ): string {
    const nonce = createNonce();
    const payload = escapeHtml(JSON.stringify({
      result,
      raw: raw ?? result,
      source,
      title,
    }));

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
        gap: 4px;
        border-bottom: 1px solid var(--vscode-panel-border);
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
        border: 1px solid var(--vscode-panel-border);
        background: var(--vscode-editor-background);
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
      </div>
      <section id="table" class="pane active" role="tabpanel"></section>
      <section id="raw" class="pane" role="tabpanel"></section>
      <section id="chart" class="pane" role="tabpanel"></section>
    </div>
    <script type="application/json" id="payload" nonce="${nonce}">${payload}</script>
    <script nonce="${nonce}">
      const data = JSON.parse(document.getElementById('payload').textContent);
      const result = data.result;
      const rows = Array.isArray(result.rows) ? result.rows : [];
      const columns = Array.isArray(result.columns) ? result.columns : [];

      document.getElementById('source-title').textContent = data.source.label;
      document.getElementById('source-text').textContent = data.source.text || '';

      if (result.error && result.error.message) {
        const error = document.getElementById('error');
        error.hidden = false;
        error.textContent = result.error.code ? result.error.code + ': ' + result.error.message : result.error.message;
      }

      for (const tab of document.querySelectorAll('[data-tab]')) {
        tab.addEventListener('click', () => activateTab(tab.dataset.tab));
      }

      renderTable();
      renderRaw();
      renderChart();

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

        const values = chart.values;
        const min = Math.min(...values);
        const max = Math.max(...values);
        const span = max === min ? 1 : max - min;
        const xStep = values.length <= 1 ? 0 : (width - pad * 2) / (values.length - 1);

        ctx.strokeStyle = getCss('--vscode-charts-blue') || '#3794ff';
        ctx.lineWidth = 2;
        ctx.beginPath();
        values.forEach((value, index) => {
          const x = pad + xStep * index;
          const y = height - pad - ((value - min) / span) * (height - pad * 2);
          if (index === 0) {
            ctx.moveTo(x, y);
          } else {
            ctx.lineTo(x, y);
          }
        });
        ctx.stroke();

        ctx.fillStyle = getCss('--vscode-foreground') || '#ddd';
        ctx.font = '12px ' + getComputedStyle(document.body).fontFamily;
        ctx.fillText(chart.yColumn + ' (' + min.toPrecision(4) + ' - ' + max.toPrecision(4) + ')', pad, 24);
        ctx.fillText(chart.xColumn, width - pad - 120, height - 12);
      }

      function inferChart() {
        const numericColumns = columns
          .map((column, index) => ({ column, index }))
          .filter((entry) => rows.some((row) => typeof Number(Array.isArray(row) ? row[entry.index] : undefined) === 'number'
            && Number.isFinite(Number(Array.isArray(row) ? row[entry.index] : undefined))));
        if (!numericColumns.length) {
          return null;
        }

        const y = numericColumns[numericColumns.length - 1];
        const x = columns
          .map((column, index) => ({ column, index }))
          .find((entry) => entry.index !== y.index) ?? { column: 'row', index: -1 };

        const values = rows
          .map((row) => Number(Array.isArray(row) ? row[y.index] : undefined))
          .filter((value) => Number.isFinite(value));
        if (!values.length) {
          return null;
        }

        return {
          xColumn: String(x.column),
          yColumn: String(y.column),
          values,
        };
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
