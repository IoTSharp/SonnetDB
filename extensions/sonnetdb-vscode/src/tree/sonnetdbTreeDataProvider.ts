import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import {
  BackupStatusInfo,
  DocumentCollectionInfo,
  FullTextIndexStat,
  IndexLifecycleInfo,
  KvEntryResponse,
  MeasurementInfo,
  MqMessageResponse,
  MqTopicInfo,
  SchemaResponse,
  SonnetDbConnectionProfile,
  TableInfo,
  VectorIndexStat,
} from '../core/types';

export type TreeNode =
  | { kind: 'empty' }
  | { kind: 'connection'; profile: SonnetDbConnectionProfile; active: boolean }
  | { kind: 'database'; profile: SonnetDbConnectionProfile; name: string; active: boolean }
  | { kind: 'section'; profile: SonnetDbConnectionProfile; database: string; section: SchemaSection; snapshot: DatabaseExplorerSnapshot }
  | { kind: 'measurement'; profile: SonnetDbConnectionProfile; database: string; measurement: MeasurementInfo }
  | { kind: 'table'; profile: SonnetDbConnectionProfile; database: string; table: TableInfo }
  | { kind: 'document'; profile: SonnetDbConnectionProfile; database: string; collection: DocumentCollectionInfo }
  | { kind: 'index'; profile: SonnetDbConnectionProfile; database: string; index: IndexLifecycleInfo }
  | { kind: 'backup'; profile: SonnetDbConnectionProfile; database: string; backupStatus: BackupStatusInfo | null }
  | { kind: 'kvKeyspace'; profile: SonnetDbConnectionProfile; database: string; keyspace: string }
  | { kind: 'kvEntry'; profile: SonnetDbConnectionProfile; database: string; keyspace: string; entry: KvEntryResponse }
  | { kind: 'vectorIndex'; profile: SonnetDbConnectionProfile; database: string; index: VectorIndexStat }
  | { kind: 'fullTextIndex'; profile: SonnetDbConnectionProfile; database: string; index: FullTextIndexStat }
  | { kind: 'mqTopic'; profile: SonnetDbConnectionProfile; database: string; topic: MqTopicInfo }
  | { kind: 'mqMessage'; profile: SonnetDbConnectionProfile; database: string; topic: string; message: MqMessageResponse }
  | { kind: 'error'; label: string; description?: string };

type SchemaSection =
  | 'measurements'
  | 'tables'
  | 'documents'
  | 'indexes'
  | 'kv'
  | 'vector'
  | 'fulltext'
  | 'mq'
  | 'backup';

interface DatabaseExplorerSnapshot {
  schema: SchemaResponse;
  kvKeyspaces: string[];
  vectorIndexes: VectorIndexStat[];
  fullTextIndexes: FullTextIndexStat[];
  mqTopics: MqTopicInfo[];
  errors: Partial<Record<SchemaSection, string>>;
}

interface SafeValue<T> {
  value: T;
  error?: string;
}

export class SonnetDbTreeDataProvider implements vscode.TreeDataProvider<TreeNode> {
  private readonly emitter = new vscode.EventEmitter<TreeNode | undefined | null | void>();

  public readonly onDidChangeTreeData = this.emitter.event;

  public constructor(
    private readonly getProfiles: () => SonnetDbConnectionProfile[],
    private readonly getToken: (profile: SonnetDbConnectionProfile) => Promise<string | undefined>,
    private readonly getActiveProfileId: () => string | undefined,
  ) {}

  public refresh(): void {
    this.emitter.fire();
  }

  public getTreeItem(element: TreeNode): vscode.TreeItem {
    if (element.kind === 'empty') {
      const item = new vscode.TreeItem('No SonnetDB connections', vscode.TreeItemCollapsibleState.None);
      item.command = {
        command: 'sonnetdb.addConnection',
        title: 'Add Connection',
      };
      item.contextValue = 'empty';
      return item;
    }

    switch (element.kind) {
      case 'connection': {
        const item = new vscode.TreeItem(element.profile.label, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = element.active ? `${element.profile.baseUrl} · active` : element.profile.baseUrl;
        item.contextValue = element.active ? 'connection.active' : 'connection';
        item.iconPath = new vscode.ThemeIcon('server');
        return item;
      }
      case 'database': {
        const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = element.active ? 'active database' : element.profile.label;
        item.contextValue = 'database';
        item.iconPath = new vscode.ThemeIcon('database');
        item.command = {
          command: 'sonnetdb.useDatabase',
          title: 'Use Database',
          arguments: [element],
        };
        return item;
      }
      case 'section': {
        const counts = getSectionCounts(element.snapshot);
        const error = element.snapshot.errors[element.section];
        const item = new vscode.TreeItem(sectionLabel(element.section), vscode.TreeItemCollapsibleState.Collapsed);
        item.description = error ? 'failed' : String(counts[element.section]);
        item.contextValue = `schemaSection.${element.section}`;
        item.iconPath = new vscode.ThemeIcon(error ? 'warning' : sectionIcon(element.section));
        if (error) {
          item.tooltip = error;
        }
        return item;
      }
      case 'measurement': {
        const item = new vscode.TreeItem(element.measurement.name, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.measurement.columns.length} columns`;
        item.contextValue = 'measurement';
        item.iconPath = new vscode.ThemeIcon('pulse');
        return item;
      }
      case 'table': {
        const item = new vscode.TreeItem(element.table.name, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.table.columns.length} columns, ${element.table.indexes.length} indexes`;
        item.contextValue = 'table';
        item.iconPath = new vscode.ThemeIcon('table');
        return item;
      }
      case 'document': {
        const item = new vscode.TreeItem(element.collection.name, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.collection.jsonIndexes.length} json, ${element.collection.fullTextIndexes.length} fulltext`;
        item.contextValue = 'documentCollection';
        item.iconPath = new vscode.ThemeIcon('json');
        return item;
      }
      case 'index': {
        const item = new vscode.TreeItem(element.index.name, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.index.owner} · ${element.index.kind} · ${element.index.state}`;
        item.contextValue = 'index';
        item.iconPath = new vscode.ThemeIcon(element.index.rebuildable ? 'tools' : 'symbol-method');
        return item;
      }
      case 'backup': {
        const item = new vscode.TreeItem('backup status', vscode.TreeItemCollapsibleState.None);
        item.description = element.backupStatus
          ? `${element.backupStatus.segmentCount} segments, ${element.backupStatus.walFileCount} WAL`
          : 'unavailable';
        item.contextValue = 'backupStatus';
        item.iconPath = new vscode.ThemeIcon('archive');
        return item;
      }
      case 'kvKeyspace': {
        const item = new vscode.TreeItem(element.keyspace, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = 'KV';
        item.contextValue = 'kvKeyspace';
        item.iconPath = new vscode.ThemeIcon('key');
        item.command = {
          command: 'sonnetdb.previewKvKeyspace',
          title: 'Preview KV Keyspace',
          arguments: [element],
        };
        return item;
      }
      case 'kvEntry': {
        const item = new vscode.TreeItem(element.entry.key, vscode.TreeItemCollapsibleState.None);
        item.description = `v${element.entry.version}${element.entry.expiresAtUtc ? ' · expires' : ''}`;
        item.contextValue = 'kvEntry';
        item.iconPath = new vscode.ThemeIcon('symbol-string');
        item.tooltip = new vscode.MarkdownString(`\`${escapeMarkdown(element.entry.key)}\`\n\n${escapeMarkdown(previewBase64(element.entry.value, 240))}`);
        return item;
      }
      case 'vectorIndex': {
        const item = new vscode.TreeItem(`${element.index.measurement}.${element.index.column}`, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.index.kind} · ${element.index.metric}${element.index.dimension ? ` · ${element.index.dimension}d` : ''}`;
        item.contextValue = 'vectorIndex';
        item.iconPath = new vscode.ThemeIcon('symbol-array');
        item.command = {
          command: 'sonnetdb.searchVectorIndex',
          title: 'Search Vector Index',
          arguments: [element],
        };
        return item;
      }
      case 'fullTextIndex': {
        const item = new vscode.TreeItem(`${element.index.collection}.${element.index.name}`, vscode.TreeItemCollapsibleState.None);
        item.description = `${element.index.tokenizer} · ${element.index.documentCount} docs`;
        item.contextValue = 'fullTextIndex';
        item.iconPath = new vscode.ThemeIcon('whole-word');
        item.command = {
          command: 'sonnetdb.searchFullTextIndex',
          title: 'Search Full Text Index',
          arguments: [element],
        };
        return item;
      }
      case 'mqTopic': {
        const item = new vscode.TreeItem(element.topic.topic, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = `${element.topic.messageCount} messages · next ${element.topic.nextOffset}`;
        item.contextValue = 'mqTopic';
        item.iconPath = new vscode.ThemeIcon('broadcast');
        item.command = {
          command: 'sonnetdb.previewMqTopic',
          title: 'Preview MQ Topic',
          arguments: [element],
        };
        return item;
      }
      case 'mqMessage': {
        const item = new vscode.TreeItem(`#${element.message.offset}`, vscode.TreeItemCollapsibleState.None);
        item.description = element.message.timestampUtc;
        item.contextValue = 'mqMessage';
        item.iconPath = new vscode.ThemeIcon('comment');
        item.tooltip = previewBase64(element.message.payload, 240);
        return item;
      }
      case 'error': {
        const item = new vscode.TreeItem(element.label, vscode.TreeItemCollapsibleState.None);
        item.description = element.description;
        item.contextValue = 'error';
        item.iconPath = new vscode.ThemeIcon('warning');
        return item;
      }
    }
  }

  public async getChildren(element?: TreeNode): Promise<TreeNode[]> {
    if (element) {
      switch (element.kind) {
        case 'connection':
          return this.loadDatabases(element.profile);
        case 'database':
          return this.loadDatabaseExplorer(element.profile, element.name);
        case 'section':
          return getSectionChildren(element);
        case 'kvKeyspace':
          return this.loadKvEntries(element);
        case 'mqTopic':
          return this.loadMqMessages(element);
        default:
          return [];
      }
    }

    const profiles = this.getProfiles();
    if (profiles.length === 0) {
      return [{ kind: 'empty' }];
    }

    const activeProfileId = this.getActiveProfileId();
    return profiles.map((profile) => ({
      kind: 'connection',
      profile,
      active: profile.id === activeProfileId,
    }));
  }

  private async loadDatabases(profile: SonnetDbConnectionProfile): Promise<TreeNode[]> {
    try {
      const token = await this.getToken(profile);
      const client = new SonnetDbClient(profile.baseUrl, token);
      const response = await client.listDatabases();
      if (response.databases.length === 0) {
        return [{ kind: 'error', label: 'No visible databases' }];
      }
      const activeDatabase = profile.defaultDatabase;
      return response.databases.map((name) => ({
        kind: 'database',
        profile,
        name,
        active: profile.id === this.getActiveProfileId() && name === activeDatabase,
      }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load databases',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }

  private async loadDatabaseExplorer(profile: SonnetDbConnectionProfile, database: string): Promise<TreeNode[]> {
    try {
      const token = await this.getToken(profile);
      const client = new SonnetDbClient(profile.baseUrl, token);
      const schema = await client.fetchSchema(database);
      const [kv, vector, fulltext, mq] = await Promise.all([
        safe(() => client.fetchKvKeyspaces(database), []),
        safe(() => client.fetchVectorIndexes(database), []),
        safe(() => client.fetchFullTextIndexes(database), []),
        safe(() => client.fetchMqTopics(database), []),
      ]);

      const snapshot: DatabaseExplorerSnapshot = {
        schema,
        kvKeyspaces: kv.value,
        vectorIndexes: vector.value,
        fullTextIndexes: fulltext.value,
        mqTopics: mq.value,
        errors: {
          kv: kv.error,
          vector: vector.error,
          fulltext: fulltext.error,
          mq: mq.error,
        },
      };
      const sections: SchemaSection[] = ['measurements', 'tables', 'documents', 'indexes', 'kv', 'vector', 'fulltext', 'mq', 'backup'];
      return sections.map((section) => ({ kind: 'section', profile, database, section, snapshot }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load schema',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }

  private async loadKvEntries(element: Extract<TreeNode, { kind: 'kvKeyspace' }>): Promise<TreeNode[]> {
    try {
      const token = await this.getToken(element.profile);
      const client = new SonnetDbClient(element.profile.baseUrl, token);
      const response = await client.scanKvEntries(element.database, element.keyspace, { limit: 50 });
      if (response.entries.length === 0) {
        return [{ kind: 'error', label: 'No keys' }];
      }
      return response.entries.map((entry) => ({
        kind: 'kvEntry',
        profile: element.profile,
        database: element.database,
        keyspace: element.keyspace,
        entry,
      }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load keys',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }

  private async loadMqMessages(element: Extract<TreeNode, { kind: 'mqTopic' }>): Promise<TreeNode[]> {
    try {
      const token = await this.getToken(element.profile);
      const client = new SonnetDbClient(element.profile.baseUrl, token);
      const maxCount = Math.min(25, Math.max(1, element.topic.messageCount));
      const fromOffset = Math.max(0, element.topic.nextOffset - maxCount);
      const response = await client.browseMqMessages(element.database, element.topic.topic, {
        fromOffset,
        maxCount,
      });
      if (response.messages.length === 0) {
        return [{ kind: 'error', label: 'No messages' }];
      }
      return response.messages.map((message) => ({
        kind: 'mqMessage',
        profile: element.profile,
        database: element.database,
        topic: element.topic.topic,
        message,
      }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load messages',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }
}

function getSectionChildren(section: Extract<TreeNode, { kind: 'section' }>): TreeNode[] {
  const error = section.snapshot.errors[section.section];
  if (error) {
    return [{ kind: 'error', label: `Failed to load ${sectionLabel(section.section)}`, description: error }];
  }

  switch (section.section) {
    case 'measurements':
      return section.snapshot.schema.measurements.map((measurement) => ({
        kind: 'measurement',
        profile: section.profile,
        database: section.database,
        measurement,
      }));
    case 'tables':
      return (section.snapshot.schema.tables ?? []).map((table) => ({
        kind: 'table',
        profile: section.profile,
        database: section.database,
        table,
      }));
    case 'documents':
      return (section.snapshot.schema.documentCollections ?? []).map((collection) => ({
        kind: 'document',
        profile: section.profile,
        database: section.database,
        collection,
      }));
    case 'indexes':
      return (section.snapshot.schema.indexes ?? []).map((index) => ({
        kind: 'index',
        profile: section.profile,
        database: section.database,
        index,
      }));
    case 'kv':
      return section.snapshot.kvKeyspaces.map((keyspace) => ({
        kind: 'kvKeyspace',
        profile: section.profile,
        database: section.database,
        keyspace,
      }));
    case 'vector':
      return section.snapshot.vectorIndexes.map((index) => ({
        kind: 'vectorIndex',
        profile: section.profile,
        database: section.database,
        index,
      }));
    case 'fulltext':
      return section.snapshot.fullTextIndexes.map((index) => ({
        kind: 'fullTextIndex',
        profile: section.profile,
        database: section.database,
        index,
      }));
    case 'mq':
      return section.snapshot.mqTopics.map((topic) => ({
        kind: 'mqTopic',
        profile: section.profile,
        database: section.database,
        topic,
      }));
    case 'backup':
      return [{
        kind: 'backup',
        profile: section.profile,
        database: section.database,
        backupStatus: section.snapshot.schema.backupStatus ?? null,
      }];
  }
}

function getSectionCounts(snapshot: DatabaseExplorerSnapshot): Record<SchemaSection, number> {
  return {
    measurements: snapshot.schema.measurements.length,
    tables: snapshot.schema.tables?.length ?? 0,
    documents: snapshot.schema.documentCollections?.length ?? 0,
    indexes: snapshot.schema.indexes?.length ?? 0,
    kv: snapshot.kvKeyspaces.length,
    vector: snapshot.vectorIndexes.length,
    fulltext: snapshot.fullTextIndexes.length,
    mq: snapshot.mqTopics.length,
    backup: snapshot.schema.backupStatus ? 1 : 0,
  };
}

function sectionLabel(section: SchemaSection): string {
  switch (section) {
    case 'measurements': return 'Measurements';
    case 'tables': return 'Tables';
    case 'documents': return 'Documents';
    case 'indexes': return 'Indexes';
    case 'kv': return 'KV Keyspaces';
    case 'vector': return 'Vector Indexes';
    case 'fulltext': return 'FullText Indexes';
    case 'mq': return 'MQ Topics';
    case 'backup': return 'Backup';
  }
}

function sectionIcon(section: SchemaSection): string {
  switch (section) {
    case 'measurements': return 'pulse';
    case 'tables': return 'table';
    case 'documents': return 'json';
    case 'indexes': return 'symbol-method';
    case 'kv': return 'key';
    case 'vector': return 'symbol-array';
    case 'fulltext': return 'whole-word';
    case 'mq': return 'broadcast';
    case 'backup': return 'archive';
  }
}

async function safe<T>(factory: () => Promise<T>, fallback: T): Promise<SafeValue<T>> {
  try {
    return { value: await factory() };
  } catch (error) {
    return {
      value: fallback,
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

function previewBase64(value: string, maxLength: number): string {
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

    return truncate(printable ? text : `${buffer.length} bytes`, maxLength);
  } catch {
    return truncate(value, maxLength);
  }
}

function truncate(value: string, maxLength: number): string {
  return value.length > maxLength ? `${value.slice(0, maxLength - 1)}...` : value;
}

function escapeMarkdown(value: string): string {
  return value.replace(/([\\`*_{}[\]()#+\-.!|>])/gu, '\\$1');
}
