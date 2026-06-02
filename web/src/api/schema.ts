import type { AxiosInstance } from 'axios';

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

export interface TableColumnInfo {
  name: string;
  dataType: string;
  isPrimaryKey: boolean;
  isNullable: boolean;
  ordinal: number;
}

export interface TableIndexInfo {
  name: string;
  columns: string[];
  isUnique: boolean;
  createdUtc: string;
  rebuildable: boolean;
  jsonPath?: string | null;
}

export interface TableInfo {
  name: string;
  columns: TableColumnInfo[];
  primaryKey: string[];
  indexes: TableIndexInfo[];
  createdUtc: string;
}

export interface DocumentJsonIndexInfo {
  name: string;
  path: string;
  createdUtc: string;
  rebuildable: boolean;
}

export interface DocumentFullTextIndexInfo {
  name: string;
  fields: string[];
  tokenizer: string;
  createdUtc: string;
  includedInBackup: boolean;
  rebuildable: boolean;
}

export interface DocumentCollectionInfo {
  name: string;
  jsonIndexes: DocumentJsonIndexInfo[];
  fullTextIndexes: DocumentFullTextIndexInfo[];
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

/** 获取指定数据库的 schema（measurement 列表及列定义），供 SQL 自动补全使用。 */
export async function fetchSchema(api: AxiosInstance, db: string): Promise<SchemaResponse> {
  const resp = await api.get<SchemaResponse>(`/v1/db/${encodeURIComponent(db)}/schema`);
  return resp.data;
}

export interface MaintenanceRequest {
  operation: string;
  targetModel?: string;
  targetOwner?: string;
  targetName?: string;
  backupDirectory?: string;
  restoreTargetDirectory?: string;
  overwrite?: boolean;
}

export interface MaintenanceCheckInfo {
  name: string;
  status: string;
  message: string;
  count?: number | null;
}

export interface BackupVerificationInfo {
  isValid: boolean;
  checkedFiles: number;
  errors: string[];
}

export interface RestoreDryRunInfo {
  isValid: boolean;
  fileCount: number;
  totalBytes: number;
  indexCount: number;
  targetDirectoryExists: boolean;
  targetDirectoryEmpty: boolean;
}

export interface IndexMaintenanceInfo {
  model: string;
  owner: string;
  name: string;
  kind: string;
  mode: string;
  planned: boolean;
  rebuildable: boolean;
  documentCount?: number | null;
}

export interface MaintenanceResponse {
  operation: string;
  status: string;
  success: boolean;
  message: string;
  completedUtc: string;
  checks: MaintenanceCheckInfo[];
  backupVerification?: BackupVerificationInfo | null;
  restoreDryRun?: RestoreDryRunInfo | null;
  index?: IndexMaintenanceInfo | null;
}

export async function runMaintenance(
  api: AxiosInstance,
  db: string,
  request: MaintenanceRequest,
): Promise<MaintenanceResponse> {
  const resp = await api.post<MaintenanceResponse>(`/v1/db/${encodeURIComponent(db)}/maintenance`, request);
  return resp.data;
}
