import * as vscode from 'vscode';
import { SonnetDbClient } from '../core/sonnetdbClient';
import {
  BackupStatusInfo,
  DocumentCollectionInfo,
  IndexLifecycleInfo,
  MeasurementInfo,
  SchemaResponse,
  SonnetDbConnectionProfile,
  TableInfo,
} from '../core/types';

type TreeNode =
  | { kind: 'empty' }
  | { kind: 'connection'; profile: SonnetDbConnectionProfile }
  | { kind: 'database'; profile: SonnetDbConnectionProfile; name: string }
  | { kind: 'section'; profile: SonnetDbConnectionProfile; database: string; section: SchemaSection; schema: SchemaResponse }
  | { kind: 'measurement'; database: string; measurement: MeasurementInfo }
  | { kind: 'table'; database: string; table: TableInfo }
  | { kind: 'document'; database: string; collection: DocumentCollectionInfo }
  | { kind: 'index'; database: string; index: IndexLifecycleInfo }
  | { kind: 'backup'; database: string; backupStatus: BackupStatusInfo | null }
  | { kind: 'error'; label: string; description?: string };

type SchemaSection = 'measurements' | 'tables' | 'documents' | 'indexes' | 'backup';

export class SonnetDbTreeDataProvider implements vscode.TreeDataProvider<TreeNode> {
  private readonly emitter = new vscode.EventEmitter<TreeNode | undefined | null | void>();

  public readonly onDidChangeTreeData = this.emitter.event;

  public constructor(
    private readonly getProfiles: () => SonnetDbConnectionProfile[],
    private readonly getToken: (profile: SonnetDbConnectionProfile) => Promise<string | undefined>,
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
        item.description = element.profile.baseUrl;
        item.contextValue = 'connection';
        item.iconPath = new vscode.ThemeIcon('server');
        return item;
      }
      case 'database': {
        const item = new vscode.TreeItem(element.name, vscode.TreeItemCollapsibleState.Collapsed);
        item.description = element.profile.label;
        item.contextValue = 'database';
        item.iconPath = new vscode.ThemeIcon('database');
        return item;
      }
      case 'section': {
        const counts = getSectionCounts(element.schema);
        const item = new vscode.TreeItem(sectionLabel(element.section), vscode.TreeItemCollapsibleState.Collapsed);
        item.description = String(counts[element.section]);
        item.contextValue = `schemaSection.${element.section}`;
        item.iconPath = new vscode.ThemeIcon(sectionIcon(element.section));
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
          return this.loadDatabaseSchema(element.profile, element.name);
        case 'section':
          return getSectionChildren(element);
        default:
          return [];
      }
    }

    const profiles = this.getProfiles();
    if (profiles.length === 0) {
      return [{ kind: 'empty' }];
    }

    return profiles.map((profile) => ({
      kind: 'connection',
      profile,
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
      return response.databases.map((name) => ({ kind: 'database', profile, name }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load databases',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }

  private async loadDatabaseSchema(profile: SonnetDbConnectionProfile, database: string): Promise<TreeNode[]> {
    try {
      const token = await this.getToken(profile);
      const client = new SonnetDbClient(profile.baseUrl, token);
      const schema = await client.fetchSchema(database);
      const sections: SchemaSection[] = ['measurements', 'tables', 'documents', 'indexes', 'backup'];
      return sections.map((section) => ({ kind: 'section', profile, database, section, schema }));
    } catch (error) {
      return [{
        kind: 'error',
        label: 'Failed to load schema',
        description: error instanceof Error ? error.message : undefined,
      }];
    }
  }
}

function getSectionChildren(section: Extract<TreeNode, { kind: 'section' }>): TreeNode[] {
  switch (section.section) {
    case 'measurements':
      return section.schema.measurements.map((measurement) => ({
        kind: 'measurement',
        database: section.database,
        measurement,
      }));
    case 'tables':
      return (section.schema.tables ?? []).map((table) => ({
        kind: 'table',
        database: section.database,
        table,
      }));
    case 'documents':
      return (section.schema.documentCollections ?? []).map((collection) => ({
        kind: 'document',
        database: section.database,
        collection,
      }));
    case 'indexes':
      return (section.schema.indexes ?? []).map((index) => ({
        kind: 'index',
        database: section.database,
        index,
      }));
    case 'backup':
      return [{
        kind: 'backup',
        database: section.database,
        backupStatus: section.schema.backupStatus ?? null,
      }];
  }
}

function getSectionCounts(schema: SchemaResponse): Record<SchemaSection, number> {
  return {
    measurements: schema.measurements.length,
    tables: schema.tables?.length ?? 0,
    documents: schema.documentCollections?.length ?? 0,
    indexes: schema.indexes?.length ?? 0,
    backup: schema.backupStatus ? 1 : 0,
  };
}

function sectionLabel(section: SchemaSection): string {
  switch (section) {
    case 'measurements': return 'Measurements';
    case 'tables': return 'Tables';
    case 'documents': return 'Documents';
    case 'indexes': return 'Indexes';
    case 'backup': return 'Backup';
  }
}

function sectionIcon(section: SchemaSection): string {
  switch (section) {
    case 'measurements': return 'pulse';
    case 'tables': return 'table';
    case 'documents': return 'json';
    case 'indexes': return 'symbol-method';
    case 'backup': return 'archive';
  }
}
