import * as vscode from 'vscode';
import { getDefaultBaseUrl, getDefaultQueryMaxRows } from './core/config';
import { ConnectionService } from './core/connectionService';
import { ManagedServerService } from './core/managedServerService';
import { SonnetDbClient } from './core/sonnetdbClient';
import { getEditorSql } from './core/sqlContext';
import { SonnetDbConnectionProfile } from './core/types';
import { registerRunQueryCommand } from './commands/runQueryCommand';
import { registerProductivityCommands } from './commands/productivityCommands';
import { registerSqlCompletionProvider } from './language/sqlCompletionProvider';
import { registerSqlLanguageFeatures } from './language/sqlLanguageFeatures';
import { createSqlLanguageServer } from './language/sqlLanguageServer';
import { CopilotPanel } from './panels/copilotPanel';
import { DocumentQueryPanel } from './panels/documentQueryPanel';
import { QueryResultPanel } from './panels/queryResultPanel';
import { SonnetDbTreeDataProvider, TreeNode } from './tree/sonnetdbTreeDataProvider';
import { ConnectionStatus } from './ui/connectionStatus';

export function activate(context: vscode.ExtensionContext): void {
  const connections = new ConnectionService(context);
  const getActiveProfile = (): SonnetDbConnectionProfile | undefined => connections.getActiveProfile();
  const getToken = (profile: SonnetDbConnectionProfile): Promise<string | undefined> => connections.getToken(profile);
  const setActiveProfile = async (profile: SonnetDbConnectionProfile, database?: string): Promise<void> => {
    await connections.setActive(profile, database);
  };
  const createClient = (profile: SonnetDbConnectionProfile): Promise<SonnetDbClient> => connections.createClient(profile);

  const tree = new SonnetDbTreeDataProvider(
    () => connections.getProfiles(),
    getToken,
    () => connections.getActiveProfileId(),
    (profile) => connections.getProbe(profile),
  );
  const resultPanel = new QueryResultPanel(context);
  const documentQueryPanel = new DocumentQueryPanel(createClient);
  const copilotPanel = new CopilotPanel(getActiveProfile, getToken);
  const managedServer = new ManagedServerService(context, connections);

  context.subscriptions.push(connections, managedServer, documentQueryPanel, new ConnectionStatus(connections));
  context.subscriptions.push(connections.onDidChange(() => tree.refresh()));

  context.subscriptions.push(
    vscode.window.registerTreeDataProvider('sonnetdb.explorer', tree),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.addConnection', async () => {
      const label = await vscode.window.showInputBox({
        prompt: 'Connection label',
        value: 'Local SonnetDB',
        ignoreFocusOut: true,
      });

      if (!label) {
        return;
      }

      const baseUrl = await vscode.window.showInputBox({
        prompt: 'SonnetDB base URL',
        value: getDefaultBaseUrl(),
        ignoreFocusOut: true,
      });

      if (!baseUrl) {
        return;
      }

      const token = await vscode.window.showInputBox({
        prompt: 'Bearer token (stored in SecretStorage)',
        password: true,
        ignoreFocusOut: true,
      });

      const profile: SonnetDbConnectionProfile = {
        id: `profile-${Date.now()}`,
        label,
        kind: 'remote',
        baseUrl: normalizeBaseUrl(baseUrl),
      };

      try {
        const probeClient = new SonnetDbClient(profile.baseUrl, token || undefined);
        const [health, setup] = await Promise.all([
          probeClient.checkHealth(),
          probeClient.fetchSetupStatus(),
        ]);
        if (health.status !== 'ok') {
          throw new Error(`Server reported ${health.status}.`);
        }
        if (setup.needsSetup) {
          void vscode.window.showWarningMessage(
            `${label} is reachable but requires first-time setup. Open ${profile.baseUrl}/admin/ to initialize it.`,
          );
        }
      } catch (error) {
        const choice = await vscode.window.showWarningMessage(
          `Could not verify ${profile.baseUrl}: ${errorMessage(error)}`,
          { modal: true },
          'Save Anyway',
        );
        if (choice !== 'Save Anyway') {
          return;
        }
      }

      await connections.add(profile, token || undefined);
      void connections.probe(profile).catch(() => undefined);
      void vscode.window.showInformationMessage(`Added SonnetDB connection "${label}".`);
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.selectConnection', async () => {
      const selected = await vscode.window.showQuickPick(
        connections.getProfiles().map((profile) => ({
          label: profile.label,
          description: profile.baseUrl,
          profile,
        })),
        { placeHolder: 'Select the active SonnetDB connection' },
      );
      if (selected) {
        await connections.setActive(selected.profile);
        void connections.probe(selected.profile).catch(() => undefined);
      } else if (connections.getProfiles().length === 0) {
        await vscode.commands.executeCommand('sonnetdb.addConnection');
      }
    }),
    vscode.commands.registerCommand('sonnetdb.selectDatabase', async () => {
      const profile = connections.getActiveProfile();
      if (!profile) {
        await vscode.commands.executeCommand('sonnetdb.selectConnection');
        return;
      }
      try {
        const databases = (await (await connections.createClient(profile)).listDatabases()).databases;
        const database = await vscode.window.showQuickPick(databases, { placeHolder: 'Select the active database' });
        if (database) {
          await connections.setActive(profile, database);
        }
      } catch (error) {
        showCommandError('Database list failed', error);
      }
    }),
    vscode.commands.registerCommand('sonnetdb.testConnection', async (node?: TreeNode) => {
      const profile = node?.kind === 'connection' ? node.profile : connections.getActiveProfile();
      if (!profile) {
        void vscode.window.showWarningMessage('Select a SonnetDB connection first.');
        return;
      }
      await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Notification, title: `Testing ${profile.label}` },
        async () => {
          try {
            const result = await connections.probe(profile);
            const setup = result.setup.needsSetup ? ' · setup required' : '';
            void vscode.window.showInformationMessage(
              `${profile.label} is healthy · ${result.health.databases} databases${setup}`,
            );
          } catch (error) {
            showCommandError(`Connection ${profile.label} failed`, error);
          }
        },
      );
    }),
    vscode.commands.registerCommand('sonnetdb.editConnection', async (node?: TreeNode) => {
      const profile = node?.kind === 'connection' ? node.profile : connections.getActiveProfile();
      if (!profile) {
        return;
      }
      const label = await vscode.window.showInputBox({ prompt: 'Connection label', value: profile.label });
      if (!label) {
        return;
      }
      const baseUrl = await vscode.window.showInputBox({ prompt: 'SonnetDB base URL', value: profile.baseUrl });
      if (!baseUrl) {
        return;
      }
      const token = await vscode.window.showInputBox({
        prompt: 'New bearer token (leave blank to keep the current token)',
        password: true,
      });
      await connections.update(profile, { label, baseUrl: normalizeBaseUrl(baseUrl) }, token || undefined);
      void connections.probe(profile).catch(() => undefined);
    }),
    vscode.commands.registerCommand('sonnetdb.removeConnection', async (node?: TreeNode) => {
      const profile = node?.kind === 'connection' ? node.profile : connections.getActiveProfile();
      if (!profile) {
        return;
      }
      const choice = await vscode.window.showWarningMessage(
        `Remove SonnetDB connection "${profile.label}" and its stored token?`,
        { modal: true },
        'Remove',
      );
      if (choice === 'Remove') {
        await connections.remove(profile);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.refreshExplorer', () => {
      tree.refresh();
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.useDatabase', async (node?: TreeNode) => {
      if (!node || node.kind !== 'database') {
        void vscode.window.showWarningMessage('Select a SonnetDB database from the Explorer first.');
        return;
      }

      await setActiveProfile(node.profile, node.name);
      void vscode.window.showInformationMessage(`Using ${node.profile.label} / ${node.name}.`);
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.openCopilot', () => {
      void copilotPanel.show();
    }),
    vscode.commands.registerCommand('sonnetdb.askCopilot', () => {
      const editor = vscode.window.activeTextEditor;
      const sql = editor ? getEditorSql(editor) : undefined;
      const prompt = sql ? `Explain this query and suggest safe improvements:\n\n${sql}` : '';
      void copilotPanel.show(prompt);
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.queryDocumentCollection', async (node?: TreeNode) => {
      if (!node || node.kind !== 'document') {
        void vscode.window.showWarningMessage('Select a Document collection from the SonnetDB Explorer first.');
        return;
      }
      try {
        await documentQueryPanel.show({
          profile: node.profile,
          database: node.database,
          collection: node.collection.name,
        });
      } catch (error) {
        showCommandError('Document query panel failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.previewKvKeyspace', async (node?: TreeNode) => {
      if (!node || node.kind !== 'kvKeyspace') {
        void vscode.window.showWarningMessage('Select a KV keyspace from the SonnetDB Explorer first.');
        return;
      }

      try {
        const client = await createClient(node.profile);
        const limit = boundedPreviewLimit();
        const response = await client.scanKvEntries(node.database, node.keyspace, { limit });
        resultPanel.showRows(
          `KV ${node.database}/${node.keyspace}`,
          ['key', 'value', 'base64', 'version', 'expiresAtUtc'],
          response.entries.map((entry) => [
            entry.key,
            decodeBase64(entry.value),
            entry.value,
            entry.version,
            entry.expiresAtUtc ?? '',
          ]),
          response,
          `POST /v1/db/${node.database}/kv/${node.keyspace}/scan`,
        );
      } catch (error) {
        showCommandError('KV preview failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.searchVectorIndex', async (node?: TreeNode) => {
      if (!node || node.kind !== 'vectorIndex') {
        void vscode.window.showWarningMessage('Select a vector index from the SonnetDB Explorer first.');
        return;
      }

      try {
        const client = await createClient(node.profile);
        const mode = await vscode.window.showQuickPick(
          [
            { label: 'Text embedding', description: 'Use /vector/embed-preview before searching', value: 'embedding' },
            { label: 'Raw vector', description: 'Paste comma-separated float values', value: 'raw' },
          ],
          { placeHolder: 'Vector query source' },
        );
        if (!mode) {
          return;
        }

        let query: number[];
        if (mode.value === 'embedding') {
          const text = await vscode.window.showInputBox({
            prompt: 'Text to embed',
            ignoreFocusOut: true,
          });
          if (!text) {
            return;
          }
          query = (await client.embedVectorText(node.database, text)).vector;
        } else {
          const raw = await vscode.window.showInputBox({
            prompt: 'Vector values',
            value: node.index.dimension ? Array.from({ length: Math.min(node.index.dimension, 4) }, () => '0').join(', ') : '',
            ignoreFocusOut: true,
          });
          if (!raw) {
            return;
          }
          query = parseVector(raw);
        }

        const topK = await promptPositiveInteger('Top K', 10);
        if (topK === undefined) {
          return;
        }

        const filter = await vscode.window.showInputBox({
          prompt: 'Optional TAG/time filter',
          ignoreFocusOut: true,
        });

        const response = await client.searchVectorPreview(node.database, {
          measurement: node.index.measurement,
          column: node.index.column,
          query,
          topK,
          metric: node.index.metric,
          filter: filter?.trim() || null,
        });

        resultPanel.showRows(
          `Vector ${node.index.measurement}.${node.index.column}`,
          ['timestampUtc', 'distance', 'tags', 'fields'],
          response.hits.map((hit) => [
            hit.timestampUtc,
            hit.distance,
            formatKeyValues(hit.tags),
            formatKeyValues(hit.fields),
          ]),
          { index: node.index, response },
          `POST /v1/db/${node.database}/vector/search-preview`,
        );
      } catch (error) {
        showCommandError('Vector search failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.searchFullTextIndex', async (node?: TreeNode) => {
      if (!node || node.kind !== 'fullTextIndex') {
        void vscode.window.showWarningMessage('Select a full-text index from the SonnetDB Explorer first.');
        return;
      }

      try {
        const client = await createClient(node.profile);
        const query = await vscode.window.showInputBox({
          prompt: 'Full-text query',
          ignoreFocusOut: true,
        });
        if (!query) {
          return;
        }

        const field = await vscode.window.showQuickPick(
          ['*', ...node.index.fields],
          { placeHolder: 'Search field' },
        );
        if (!field) {
          return;
        }

        const mode = await vscode.window.showQuickPick(['exact', 'fuzzy'], {
          placeHolder: 'Search mode',
        });
        if (!mode) {
          return;
        }

        const queryKind = await vscode.window.showQuickPick(['all', 'any', 'phrase'], {
          placeHolder: 'Query kind',
        });
        if (!queryKind) {
          return;
        }

        const topK = await promptPositiveInteger('Top K', 10);
        if (topK === undefined) {
          return;
        }

        const response = await client.searchFullTextPreview(node.database, {
          collection: node.index.collection,
          index: node.index.name,
          field,
          query,
          topK,
          mode: mode as 'exact' | 'fuzzy',
          queryKind: queryKind as 'all' | 'any' | 'phrase',
        });

        resultPanel.showRows(
          `FullText ${node.index.collection}.${node.index.name}`,
          ['documentId', 'score'],
          response.hits.map((hit) => [hit.documentId, hit.score]),
          { index: node.index, response },
          `POST /v1/db/${node.database}/fulltext/search-preview`,
        );
      } catch (error) {
        showCommandError('Full-text search failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.analyzeFullText', async (node?: TreeNode) => {
      const activeProfile = getActiveProfile();
      const profile = node && node.kind === 'fullTextIndex' ? node.profile : activeProfile;
      const database = node && node.kind === 'fullTextIndex' ? node.database : activeProfile?.defaultDatabase;
      if (!profile || !database) {
        void vscode.window.showWarningMessage('Select an active SonnetDB database first.');
        return;
      }

      try {
        const tokenizer = await vscode.window.showQuickPick(['unicode', 'cjk', 'jieba'], {
          placeHolder: 'Tokenizer',
        });
        if (!tokenizer) {
          return;
        }

        const text = await vscode.window.showInputBox({
          prompt: 'Text to analyze',
          ignoreFocusOut: true,
        });
        if (text === undefined) {
          return;
        }

        const client = await createClient(profile);
        const response = await client.analyzeFullText(database, { tokenizer, text });
        resultPanel.showRows(
          `Analyze ${tokenizer}`,
          ['text', 'startOffset', 'endOffset', 'positionIncrement'],
          response.tokens.map((token) => [
            token.text,
            token.startOffset,
            token.endOffset,
            token.positionIncrement,
          ]),
          response,
          `POST /v1/db/${database}/fulltext/analyze`,
        );
      } catch (error) {
        showCommandError('Full-text analyze failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.previewMqTopic', async (node?: TreeNode) => {
      if (!node || node.kind !== 'mqTopic') {
        void vscode.window.showWarningMessage('Select an MQ topic from the SonnetDB Explorer first.');
        return;
      }

      try {
        const client = await createClient(node.profile);
        const monitor = await client.fetchMqMonitor(node.database, node.topic.topic);
        const maxCount = Math.min(boundedPreviewLimit(), Math.max(1, monitor.messageCount));
        const fromOffset = Math.max(monitor.retainedStartOffset, monitor.nextOffset - maxCount);
        const response = await client.browseMqMessages(node.database, node.topic.topic, {
          fromOffset,
          maxCount,
        });

        resultPanel.showRows(
          `MQ ${node.database}/${node.topic.topic}`,
          ['offset', 'timestampUtc', 'payload', 'payloadBase64', 'headers'],
          response.messages.map((message) => [
            message.offset,
            message.timestampUtc,
            decodeBase64(message.payload),
            message.payload,
            JSON.stringify(message.headers ?? {}),
          ]),
          { monitor, response },
          `POST /v1/db/${node.database}/mq/${node.topic.topic}/browse`,
        );
      } catch (error) {
        showCommandError('MQ preview failed', error);
      }
    }),
  );

  context.subscriptions.push(
    vscode.commands.registerCommand('sonnetdb.startManagedLocalServer', () => managedServer.start()),
    vscode.commands.registerCommand('sonnetdb.stopManagedLocalServer', () => managedServer.stop()),
    vscode.commands.registerCommand('sonnetdb.showManagedServerOutput', () => managedServer.showOutput()),
  );

  registerRunQueryCommand(
    context,
    getActiveProfile,
    getToken,
    resultPanel,
    (profile, database) => connections.setActive(profile, database),
  );
  registerSqlCompletionProvider(context, getActiveProfile, createClient);
  registerSqlLanguageFeatures(context, createSqlLanguageServer(context));
  registerProductivityCommands(context, getActiveProfile, createClient, resultPanel);

  const activeProfile = connections.getActiveProfile();
  if (activeProfile) {
    void connections.probe(activeProfile).catch(() => undefined);
  }
}

export function deactivate(): void {}

function boundedPreviewLimit(): number {
  return Math.min(1000, Math.max(1, getDefaultQueryMaxRows()));
}

async function promptPositiveInteger(prompt: string, defaultValue: number): Promise<number | undefined> {
  const raw = await vscode.window.showInputBox({
    prompt,
    value: String(defaultValue),
    ignoreFocusOut: true,
    validateInput: (value) => {
      const parsed = Number(value);
      return Number.isInteger(parsed) && parsed > 0 ? undefined : 'Enter a positive integer.';
    },
  });

  if (raw === undefined) {
    return undefined;
  }

  return Number(raw);
}

function parseVector(value: string): number[] {
  const trimmed = value.trim().replace(/^\[/u, '').replace(/\]$/u, '');
  const parts = trimmed.split(/[,\s]+/u).filter(Boolean);
  const vector = parts.map((part) => Number(part));
  if (vector.length === 0 || vector.some((entry) => !Number.isFinite(entry))) {
    throw new Error('Vector must contain comma-separated numeric values.');
  }
  return vector;
}

function decodeBase64(value: string): string {
  try {
    const buffer = Buffer.from(value, 'base64');
    if (buffer.length === 0) {
      return '';
    }

    const text = buffer.toString('utf8');
    const printable = Array.from(text).every((char) => {
      const code = char.charCodeAt(0);
      return code === 9 || code === 10 || code === 13 || code >= 32;
    });

    return printable ? text : `${buffer.length} bytes`;
  } catch {
    return value;
  }
}

function formatKeyValues(values: Array<{ key: string; value: string }> | null | undefined): string {
  if (!values || values.length === 0) {
    return '';
  }
  return values.map((entry) => `${entry.key}=${entry.value}`).join(', ');
}

function showCommandError(title: string, error: unknown): void {
  void vscode.window.showErrorMessage(`${title}: ${errorMessage(error)}`);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function normalizeBaseUrl(value: string): string {
  return value.trim().replace(/\/+$/u, '');
}
