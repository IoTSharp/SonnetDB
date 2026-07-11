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

export interface DocumentFilter {
  path?: string;
  op?: string;
  value?: unknown;
  and?: DocumentFilter[];
  or?: DocumentFilter[];
  not?: DocumentFilter;
}

export interface DocumentProjection {
  name?: string;
  path?: string;
}

export interface DocumentSort {
  path: string;
  descending?: boolean;
}

export interface DocumentFindRequest {
  id?: string;
  ids?: string[];
  filter?: DocumentFilter;
  projection?: DocumentProjection[];
  sort?: DocumentSort[];
  limit?: number;
  skip?: number;
  continuationToken?: string;
}

export interface DocumentItemResponse {
  id: string;
  document: unknown;
  version: number;
}

export interface DocumentFindResponse {
  collection: string;
  documents: DocumentItemResponse[];
  count: number;
  limit?: number | null;
  skip: number;
  continuationToken?: string | null;
  hasMore: boolean;
  batchSize?: number | null;
  snapshotVersion?: number | null;
  cursorExpiresAtUtc?: string | null;
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

export interface KvKeyspaceListResponse {
  keyspaces: string[];
}

export interface KvEntryResponse {
  key: string;
  value: string;
  version: number;
  expiresAtUtc?: string | null;
}

export interface KvScanCursorRequest {
  prefix?: string | null;
  cursor?: string | null;
  limit?: number | null;
}

export interface KvScanCursorResponse {
  entries: KvEntryResponse[];
  nextCursor?: string | null;
  hasMore: boolean;
}

export interface VectorIndexStat {
  measurement: string;
  column: string;
  kind: string;
  dimension?: number | null;
  metric: string;
  params: KeyValueInfo[];
  rowCount?: number | null;
}

export interface VectorIndexStatResponse {
  indexes: VectorIndexStat[];
}

export interface VectorSearchPreviewRequest {
  measurement: string;
  column: string;
  query: number[];
  topK?: number | null;
  metric?: string | null;
  filter?: string | null;
}

export interface VectorSearchPreviewHit {
  timestampUtc: number;
  distance: number;
  tags?: KeyValueInfo[] | null;
  fields?: KeyValueInfo[] | null;
}

export interface VectorSearchPreviewResponse {
  hits: VectorSearchPreviewHit[];
}

export interface VectorEmbedPreviewResponse {
  vector: number[];
  dimension: number;
}

export interface FullTextIndexStat {
  collection: string;
  name: string;
  fields: string[];
  tokenizer: string;
  documentCount: number;
  termCount?: number | null;
}

export interface FullTextIndexStatResponse {
  indexes: FullTextIndexStat[];
}

export type FullTextSearchMode = 'exact' | 'fuzzy';
export type FullTextQueryKind = 'all' | 'any' | 'phrase';

export interface FullTextSearchPreviewRequest {
  collection: string;
  index: string;
  field: string;
  query: string;
  topK?: number | null;
  mode?: FullTextSearchMode | null;
  queryKind?: FullTextQueryKind | null;
}

export interface FullTextSearchPreviewHit {
  documentId: string;
  score: number;
}

export interface FullTextSearchPreviewResponse {
  hits: FullTextSearchPreviewHit[];
}

export interface FullTextAnalyzeRequest {
  tokenizer: string;
  text: string;
}

export interface FullTextTokenInfo {
  text: string;
  startOffset: number;
  endOffset: number;
  positionIncrement: number;
}

export interface FullTextAnalyzeResponse {
  tokens: FullTextTokenInfo[];
}

export interface MqTopicInfo {
  topic: string;
  messageCount: number;
  nextOffset: number;
}

export interface MqTopicListResponse {
  topics: MqTopicInfo[];
}

export interface MqMessageResponse {
  topic: string;
  offset: number;
  timestampUtc: string;
  headers: Record<string, string>;
  payload: string;
}

export interface MqBrowseRequest {
  fromOffset?: number | null;
  maxCount?: number | null;
}

export interface MqBrowseResponse {
  messages: MqMessageResponse[];
}

export interface MqConsumerMonitorInfo {
  consumerGroup: string;
  committedOffset: number;
  lag: number;
  progressRatio: number;
  status: string;
}

export interface MqRetentionPolicyInfo {
  maxAgeMilliseconds?: number | null;
  maxBytes?: number | null;
  retentionIntervalMilliseconds: number;
  trimAcknowledgedMessages: boolean;
  ackRetentionMinOffsetDelta: number;
  segmentMaxBytes: number;
  hotTailMaxBytes: number;
  segmentCacheSize: number;
}

export interface MqDeadLetterInfo {
  mode: string;
  candidateTopics: string[];
  activeTopic?: string | null;
}

export interface MqMonitorResponse {
  topic: string;
  messageCount: number;
  nextOffset: number;
  retainedStartOffset: number;
  consumers: MqConsumerMonitorInfo[];
  retention: MqRetentionPolicyInfo;
  deadLetter: MqDeadLetterInfo;
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

export interface BulkIngestResponse {
  written: number;
  skipped: number;
  elapsedMilliseconds: number;
}

export interface HealthResponse {
  status: string;
  databases: number;
  uptimeSeconds: number;
  copilotEnabled: boolean;
  copilotReady: boolean;
}

export interface SetupStatusResponse {
  needsSetup: boolean;
  suggestedServerId: string;
  serverId?: string | null;
  organization?: string | null;
  userCount: number;
  databaseCount: number;
}

export interface ConnectionProbeResult {
  health: HealthResponse;
  setup: SetupStatusResponse;
  checkedAt: number;
}

export interface CopilotModelsResponse {
  default: string;
  candidates: string[];
}

export interface CopilotKnowledgeStatusResponse {
  enabled: boolean;
  embeddingProvider: string;
  embeddingFallback: boolean;
  vectorDimension: number;
  docsRoots: string[];
  indexedFiles: number;
  indexedChunks: number;
  lastIngestedUtc?: string | null;
  skillCount: number;
}

export interface CopilotChatEvent {
  type: string;
  message?: string | null;
  answer?: string | null;
  toolName?: string | null;
  toolArguments?: string | null;
  toolResult?: string | null;
  skillNames?: string[] | null;
  toolNames?: string[] | null;
  citations?: Array<Record<string, unknown>> | null;
  attempt?: number | null;
}
