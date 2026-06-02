export interface SonnetDbConnectionProfile {
  id: string;
  label: string;
  kind: 'remote' | 'managed-local';
  baseUrl: string;
  defaultDatabase?: string;
  tokenSecretKey?: string;
  dataRoot?: string;
}

export interface DatabaseListResponse {
  databases: string[];
}

export interface ColumnInfo {
  name: string;
  role: string;
  dataType: string;
  vectorDimension?: number | null;
  vectorIndex?: VectorIndexInfo | null;
}

export interface MeasurementInfo {
  name: string;
  columns: ColumnInfo[];
}

export interface KeyValueInfo {
  key: string;
  value: string;
}

export interface VectorIndexInfo {
  kind: string;
  options: KeyValueInfo[];
}

export interface TableInfo {
  name: string;
  columns: Array<{
    name: string;
    dataType: string;
    isPrimaryKey: boolean;
    isNullable: boolean;
    ordinal: number;
  }>;
  primaryKey: string[];
  indexes: Array<{
    name: string;
    columns: string[];
    isUnique: boolean;
    createdUtc: string;
    rebuildable: boolean;
    jsonPath?: string | null;
  }>;
  createdUtc: string;
}

export interface DocumentCollectionInfo {
  name: string;
  jsonIndexes: Array<{
    name: string;
    path: string;
    createdUtc: string;
    rebuildable: boolean;
  }>;
  fullTextIndexes: Array<{
    name: string;
    fields: string[];
    tokenizer: string;
    createdUtc: string;
    includedInBackup: boolean;
    rebuildable: boolean;
  }>;
  createdUtc: string;
}

export interface IndexLifecycleInfo {
  id: string;
  model: string;
  owner: string;
  name: string;
  kind: string;
  state: string;
  includedInBackup: boolean;
  rebuildable: boolean;
  createdUtc?: string | null;
  columns: string[];
  detail?: string | null;
}

export interface BackupStatusInfo {
  backupCapable: boolean;
  hasRestoreManifest: boolean;
  restoreManifestCreatedUtc?: string | null;
  segmentCount: number;
  walFileCount: number;
  totalBytes: number;
  memTablePointCount: number;
  checkpointLsn: number;
  nextSegmentId: number;
}

export interface SchemaResponse {
  measurements: MeasurementInfo[];
  tables?: TableInfo[];
  documentCollections?: DocumentCollectionInfo[];
  indexes?: IndexLifecycleInfo[];
  backupStatus?: BackupStatusInfo | null;
}

export interface SqlEnd {
  type: 'end';
  rowCount: number;
  recordsAffected: number;
  elapsedMs: number;
}

export interface SqlError {
  type?: 'error';
  code?: string;
  message: string;
}

export interface SqlResultSet {
  columns: string[];
  rows: unknown[][];
  end: SqlEnd | null;
  error: SqlError | null;
  hasColumns: boolean;
}

export interface HealthResponse {
  status: string;
  databases: number;
  uptimeSeconds: number;
  copilotEnabled: boolean;
  copilotReady: boolean;
}

export interface CopilotModelsResponse {
  default: string;
  candidates: string[];
}

export interface CopilotChatEvent {
  type: string;
  message?: string | null;
  answer?: string | null;
  toolName?: string | null;
  toolArguments?: string | null;
  toolResult?: string | null;
}
