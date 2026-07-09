import * as vscode from 'vscode';
import { getDefaultBaseUrl, getDefaultQueryMaxRows } from './core/config';
import { SonnetDbClient } from './core/sonnetdbClient';
import { SonnetDbConnectionProfile } from './core/types';
import { registerRunQueryCommand } from './commands/runQueryCommand';
import { CopilotPanel } from './panels/copilotPanel';
import { QueryResultPanel } from './panels/queryResultPanel';
import { SonnetDbTreeDataProvider, TreeNode } from './tree/sonnetdbTreeDataProvider';

const ProfilesStorageKey = 'sonnetdb.connectionProfiles';
const ActiveProfileStorageKey = 'sonnetdb.activeProfileId';

export function activate(context: vscode.ExtensionContext): void {
  const profiles = loadProfiles(context);
  let activeProfileId = context.globalState.get<string>(ActiveProfileStorageKey) ?? profiles[0]?.id;

  const getActiveProfile = (): SonnetDbConnectionProfile | undefined =>
    profiles.find((profile) => profile.id === activeProfileId);
  const getToken = async (profile: SonnetDbConnectionProfile): Promise<string | undefined> =>
    context.secrets.get(getSecretKey(profile.id));
  const persistProfiles = async (): Promise<void> => {
    await context.globalState.update(ProfilesStorageKey, profiles.map(cloneProfile));
  };
  const setActiveProfile = async (profile: SonnetDbConnectionProfile, database?: string): Promise<void> => {
    activeProfileId = profile.id;
    if (database) {
      profile.defaultDatabase = database;
    }
    await persistProfiles();
    await context.globalState.update(ActiveProfileStorageKey, activeProfileId);
    tree.refresh();
  };
  const createClient = async (profile: SonnetDbConnectionProfile): Promise<SonnetDbClient> =>
    new SonnetDbClient(profile.baseUrl, await getToken(profile));

  const tree = new SonnetDbTreeDataProvider(
    () => profiles,
    getToken,
    () => activeProfileId,
  );
  const resultPanel = new QueryResultPanel();
  const copilotPanel = new CopilotPanel(getActiveProfile, getToken);

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

      const profile: SonnetDbConnectionProfile = {
        id: `profile-${Date.now()}`,
        label,
        kind: 'remote',
        baseUrl,
      };

      profiles.push(profile);
      await setActiveProfile(profile);

      const token = await vscode.window.showInputBox({
        prompt: 'Bearer token (stored in SecretStorage)',
        password: true,
        ignoreFocusOut: true,
      });

      if (token) {
        await context.secrets.store(getSecretKey(profile.id), token);
      }

      void vscode.window.showInformationMessage(`Added SonnetDB connection "${label}".`);
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
    vscode.commands.registerCommand('sonnetdb.startManagedLocalServer', () => {
      void vscode.window.showInformationMessage(
        'Managed local server mode is planned for PR #105.',
      );
    }),
  );

  registerRunQueryCommand(
    context,
    getActiveProfile,
    getToken,
    resultPanel,
  );
}

export function deactivate(): void {}

function loadProfiles(context: vscode.ExtensionContext): SonnetDbConnectionProfile[] {
  const stored = context.globalState.get<SonnetDbConnectionProfile[]>(ProfilesStorageKey, []);
  return Array.isArray(stored) ? stored.filter(isProfile) : [];
}

function isProfile(value: unknown): value is SonnetDbConnectionProfile {
  if (!value || typeof value !== 'object') {
    return false;
  }
  const record = value as Record<string, unknown>;
  return typeof record.id === 'string'
    && typeof record.label === 'string'
    && typeof record.baseUrl === 'string'
    && (record.kind === 'remote' || record.kind === 'managed-local');
}

function cloneProfile(profile: SonnetDbConnectionProfile): SonnetDbConnectionProfile {
  return {
    id: profile.id,
    label: profile.label,
    kind: profile.kind,
    baseUrl: profile.baseUrl,
    defaultDatabase: profile.defaultDatabase,
    tokenSecretKey: profile.tokenSecretKey,
    dataRoot: profile.dataRoot,
  };
}

function getSecretKey(profileId: string): string {
  return `sonnetdb.connection.${profileId}.token`;
}

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
  const message = error instanceof Error ? error.message : String(error);
  void vscode.window.showErrorMessage(`${title}: ${message}`);
}
