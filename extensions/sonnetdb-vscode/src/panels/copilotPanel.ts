import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import { CopilotChatEvent, SonnetDbConnectionProfile } from '../core/types';

type CopilotMode = 'read-only' | 'read-write';

interface CopilotWebviewAskMessage {
  type: 'ask';
  text: string;
  mode: CopilotMode;
  model?: string;
}

type CopilotWebviewMessage = CopilotWebviewAskMessage;

export class CopilotPanel {
  public constructor(
    private readonly getActiveProfile: () => SonnetDbConnectionProfile | undefined,
    private readonly getToken: (profile: SonnetDbConnectionProfile) => Promise<string | undefined>,
  ) {}

  public async show(): Promise<void> {
    const profile = this.getActiveProfile();
    if (!profile) {
      void vscode.window.showWarningMessage('No active SonnetDB connection is selected yet.');
      return;
    }

    const database = await vscode.window.showInputBox({
      prompt: 'Target database for SonnetDB Copilot',
      value: profile.defaultDatabase ?? '',
      ignoreFocusOut: true,
    });

    if (!database) {
      return;
    }

    const token = await this.getToken(profile);
    const client = new SonnetDbClient(profile.baseUrl, token);
    const models = await this.tryFetchModels(client);
    const messages: Array<{ role: string; content: string }> = [];

    const panel = vscode.window.createWebviewPanel(
      'sonnetdb.copilot',
      'SonnetDB Copilot',
      vscode.ViewColumn.Beside,
      {
        enableScripts: true,
      },
    );

    panel.webview.html = this.renderHtml(profile, database, models);

    panel.webview.onDidReceiveMessage(async (message: CopilotWebviewMessage) => {
      if (message.type !== 'ask') {
        return;
      }

      const text = message.text.trim();
      if (!text) {
        return;
      }

      if (message.mode === 'read-write') {
        const approved = await vscode.window.showWarningMessage(
          'Allow SonnetDB Copilot to run in read-write mode for this request?',
          { modal: true },
          'Allow',
        );
        if (approved !== 'Allow') {
          await panel.webview.postMessage({ type: 'error', message: 'Read-write request cancelled.' });
          return;
        }
      }

      messages.push({ role: 'user', content: text });
      await panel.webview.postMessage({ type: 'start' });

      let answer = '';
      try {
        await client.streamCopilot(
          database,
          messages,
          (event) => {
            answer += extractAssistantText(event);
            void panel.webview.postMessage({ type: 'event', event });
          },
          message.mode,
          message.model || undefined,
        );

        if (answer.trim().length > 0) {
          messages.push({ role: 'assistant', content: answer });
        }
        await panel.webview.postMessage({ type: 'done' });
      } catch (error) {
        const errorMessage = error instanceof Error ? error.message : String(error);
        await panel.webview.postMessage({ type: 'error', message: errorMessage });
      }
    });
  }

  private async tryFetchModels(client: SonnetDbClient): Promise<{ defaultModel: string; candidates: string[] }> {
    try {
      const response = await client.fetchCopilotModels();
      return {
        defaultModel: response.default,
        candidates: Array.isArray(response.candidates) ? response.candidates : [],
      };
    } catch {
      return {
        defaultModel: '',
        candidates: [],
      };
    }
  }

  private renderHtml(
    profile: SonnetDbConnectionProfile,
    database: string,
    models: { defaultModel: string; candidates: string[] },
  ): string {
    const nonce = createNonce();
    const modelOptions = models.candidates
      .map((model) => `<option value="${escapeHtml(model)}"${model === models.defaultModel ? ' selected' : ''}>${escapeHtml(model)}</option>`)
      .join('');

    return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>SonnetDB Copilot</title>
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
        display: grid;
        grid-template-rows: auto 1fr auto;
        height: 100vh;
      }
      header {
        padding: 10px 12px;
        border-bottom: 1px solid var(--vscode-panel-border);
        background: var(--vscode-sideBar-background);
      }
      header strong {
        display: block;
        font-size: 13px;
      }
      header span {
        color: var(--vscode-descriptionForeground);
        font-size: 12px;
      }
      main {
        overflow: auto;
        padding: 12px;
      }
      .entry {
        margin-bottom: 12px;
        padding: 10px;
        border: 1px solid var(--vscode-panel-border);
        background: var(--vscode-editor-inactiveSelectionBackground);
      }
      .entry.user {
        border-left: 3px solid var(--vscode-charts-blue);
      }
      .entry.assistant {
        border-left: 3px solid var(--vscode-charts-green);
      }
      .entry.tool {
        border-left: 3px solid var(--vscode-charts-yellow);
      }
      .entry.error {
        border-left: 3px solid var(--vscode-errorForeground);
        color: var(--vscode-errorForeground);
      }
      .entry h3 {
        margin: 0 0 6px;
        font-size: 12px;
      }
      .entry pre {
        margin: 0;
        white-space: pre-wrap;
        word-break: break-word;
        font-family: var(--vscode-editor-font-family);
        font-size: var(--vscode-editor-font-size);
      }
      form {
        display: grid;
        gap: 8px;
        padding: 10px;
        border-top: 1px solid var(--vscode-panel-border);
        background: var(--vscode-sideBar-background);
      }
      .controls {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
      }
      textarea,
      select,
      button {
        font: inherit;
      }
      textarea {
        width: 100%;
        min-height: 84px;
        box-sizing: border-box;
        resize: vertical;
        color: var(--vscode-input-foreground);
        background: var(--vscode-input-background);
        border: 1px solid var(--vscode-input-border);
        padding: 8px;
      }
      select {
        color: var(--vscode-dropdown-foreground);
        background: var(--vscode-dropdown-background);
        border: 1px solid var(--vscode-dropdown-border);
        padding: 4px 8px;
      }
      button {
        color: var(--vscode-button-foreground);
        background: var(--vscode-button-background);
        border: 0;
        padding: 6px 12px;
        cursor: pointer;
      }
      button:disabled {
        opacity: 0.6;
        cursor: wait;
      }
    </style>
  </head>
  <body>
    <div class="shell">
      <header>
        <strong>${escapeHtml(profile.label)} / ${escapeHtml(database)}</strong>
        <span>${escapeHtml(profile.baseUrl)}</span>
      </header>
      <main id="messages"></main>
      <form id="composer">
        <textarea id="prompt" placeholder="Ask SonnetDB Copilot"></textarea>
        <div class="controls">
          <select id="mode" aria-label="Mode">
            <option value="read-only" selected>read-only</option>
            <option value="read-write">read-write</option>
          </select>
          <select id="model" aria-label="Model">
            <option value="">default${models.defaultModel ? ` (${escapeHtml(models.defaultModel)})` : ''}</option>
            ${modelOptions}
          </select>
          <button id="send" type="submit">Send</button>
        </div>
      </form>
    </div>
    <script nonce="${nonce}">
      const vscode = acquireVsCodeApi();
      const messages = document.getElementById('messages');
      const form = document.getElementById('composer');
      const prompt = document.getElementById('prompt');
      const send = document.getElementById('send');

      form.addEventListener('submit', (event) => {
        event.preventDefault();
        const text = prompt.value.trim();
        if (!text) {
          return;
        }
        append('user', 'You', text);
        prompt.value = '';
        send.disabled = true;
        vscode.postMessage({
          type: 'ask',
          text,
          mode: document.getElementById('mode').value,
          model: document.getElementById('model').value,
        });
      });

      window.addEventListener('message', (event) => {
        const message = event.data;
        if (message.type === 'event') {
          renderCopilotEvent(message.event);
          return;
        }
        if (message.type === 'error') {
          append('error', 'Error', message.message || 'Request failed.');
          send.disabled = false;
          return;
        }
        if (message.type === 'done') {
          send.disabled = false;
        }
      });

      function renderCopilotEvent(event) {
        if (event.type === 'done') {
          return;
        }
        const text = event.answer || event.message || '';
        if (text) {
          append('assistant', event.type || 'assistant', text);
        }
        if (event.toolName) {
          append('tool', event.toolName, [event.toolArguments, event.toolResult].filter(Boolean).join('\\n\\n'));
        }
        if (!text && !event.toolName) {
          append('tool', event.type || 'event', JSON.stringify(event, null, 2));
        }
      }

      function append(kind, title, text) {
        const item = document.createElement('section');
        item.className = 'entry ' + kind;
        const heading = document.createElement('h3');
        heading.textContent = title;
        const body = document.createElement('pre');
        body.textContent = text;
        item.append(heading, body);
        messages.appendChild(item);
        messages.scrollTop = messages.scrollHeight;
      }
    </script>
  </body>
</html>`;
  }
}

function extractAssistantText(event: CopilotChatEvent): string {
  if (typeof event.answer === 'string') {
    return event.answer;
  }
  return '';
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
