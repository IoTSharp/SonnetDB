import type { AxiosInstance } from 'axios';
import type { DocumentValidator } from '@/api/documents';

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
  isRowVersion?: boolean;
  isAutoIncrement?: boolean;
  defaultExpressionSql?: string | null;
}

export interface TableIndexInfo {
  name: string;
  columns: string[];
  isUnique: boolean;
  createdUtc: string;
  rebuildable: boolean;
  jsonPath?: string | null;
}

export interface TableForeignKeyInfo {
  name: string;
  columns: string[];
  principalTable: string;
  principalColumns: string[];
  onDelete: string;
}

export interface TableInfo {
  name: string;
  columns: TableColumnInfo[];
  primaryKey: string[];
  indexes: TableIndexInfo[];
  createdUtc: string;
  foreignKeys?: TableForeignKeyInfo[];
}

export interface DocumentJsonIndexInfo {
  name: string;
  path: string;
  createdUtc: string;
  rebuildable: boolean;
  paths?: string[] | null;
  isUnique?: boolean;
  isSparse?: boolean;
  isPartial?: boolean;
  partialFilter?: string | null;
  isTtl?: boolean;
  ttlSeconds?: number | null;
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
  validator?: DocumentValidator | null;
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
  measurementSchemaRevision?: string;
  tables?: TableInfo[];
  documentCollections?: DocumentCollectionInfo[];
  indexes?: IndexLifecycleInfo[];
  backupStatus?: BackupStatusInfo | null;
}

/** 获取持久化 measurement schema 的版本，用于校验工作台缓存。 */
export async function fetchMeasurementSchemaRevision(api: AxiosInstance, db: string): Promise<string> {
  const resp = await api.get<{ revision: string }>(`/v1/db/${encodeURIComponent(db)}/schema/measurements/revision`);
  return resp.data.revision;
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

export interface BackupScopeOptions {
  includeMeasurements: boolean;
  includeTables: boolean;
  includeKeyValue: boolean;
  includeDocuments: boolean;
  includeObjects: boolean;
  includeMessageQueue: boolean;
  includeGraphs: boolean;
  includeFullTextIndexes: boolean;
  measurements: string[];
  tables: string[];
  keyspaces: string[];
  documentCollections: string[];
  buckets: string[];
  graphs: string[];
}

export interface BackupTaskDefinition {
  id: string;
  name: string;
  enabled: boolean;
  localTime: string;
  timeZoneId: string;
  destinationRoot: string;
  databaseNames: string[];
  scope: BackupScopeOptions;
  retentionDays: number;
  retentionCount: number;
  verifyAfterCreate: boolean;
}

export interface BackupTaskState {
  taskId: string;
  lastStartedUtc?: string | null;
  lastCompletedUtc?: string | null;
  status: string;
  lastOutputDirectory?: string | null;
  lastError?: string | null;
}

export interface BackupTaskResponse {
  definition: BackupTaskDefinition;
  state: BackupTaskState;
}

export interface BackupTaskListResponse {
  items: BackupTaskResponse[];
}

export async function listBackupTasks(api: AxiosInstance): Promise<BackupTaskListResponse> {
  const resp = await api.get<BackupTaskListResponse>('/v1/system/backup-tasks');
  return resp.data;
}

export async function createBackupTask(api: AxiosInstance, task: BackupTaskDefinition): Promise<BackupTaskResponse> {
  const resp = await api.post<BackupTaskResponse>('/v1/system/backup-tasks', task);
  return resp.data;
}

export async function runBackupTask(api: AxiosInstance, id: string): Promise<BackupTaskResponse> {
  const resp = await api.post<BackupTaskResponse>(`/v1/system/backup-tasks/${encodeURIComponent(id)}/run`);
  return resp.data;
}

export async function restoreBackup(api: AxiosInstance, request: { path?: string; url?: string }): Promise<BackupRestoreResponse> {
  const resp = await api.post<BackupRestoreResponse>('/v1/system/backup-restore', request);
  return resp.data;
}

export async function uploadBackup(api: AxiosInstance, file: File): Promise<BackupRestoreResponse> {
  const form = new FormData();
  form.append('file', file);
  const resp = await api.post<BackupRestoreResponse>('/v1/system/backup-restore/upload', form, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return resp.data;
}

export async function renameDatabase(api: AxiosInstance, database: string, name: string): Promise<void> {
  await api.put(`/v1/system/databases/${encodeURIComponent(database)}/name`, { name });
}

export interface BackupRestoreResponse {
  databaseName: string;
  source: string;
  verified: boolean;
  checkedFiles: number;
  totalBytes: number;
  completedUtc: string;
}
