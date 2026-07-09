import type {
  FullTextIndexStat,
  MqTopicInfo,
  ObjectBucketInfo,
  VectorIndexStat,
} from '@/api/management';
import type {
  BackupStatusInfo,
  DocumentCollectionInfo,
  IndexLifecycleInfo,
  MeasurementInfo,
  SchemaResponse,
  TableInfo,
} from '@/api/schema';

export type ExplorerModel =
  | 'measurement'
  | 'table'
  | 'document'
  | 'kv'
  | 'index'
  | 'vector'
  | 'fulltext'
  | 'mq'
  | 'bucket'
  | 'backup';

export interface ManagementExplorerInfo {
  kvKeyspaces: string[];
  vectorIndexes: VectorIndexStat[];
  fullTextIndexes: FullTextIndexStat[];
  mqTopics: MqTopicInfo[];
  buckets: ObjectBucketInfo[];
  error: string;
}

export interface ExplorerItem {
  key: string;
  model: ExplorerModel;
  name: string;
  meta: string;
  title: string;
  className: string;
  payload:
    | MeasurementInfo
    | TableInfo
    | DocumentCollectionInfo
    | IndexLifecycleInfo
    | VectorIndexStat
    | FullTextIndexStat
    | MqTopicInfo
    | ObjectBucketInfo
    | null;
}

export interface ExplorerGroup {
  key: string;
  label: string;
  count: number;
  items: ExplorerItem[];
}

export interface DatabaseTreeNode {
  name: string;
  meta: string;
  measurements: MeasurementInfo[];
  tables: TableInfo[];
  documents: DocumentCollectionInfo[];
  indexes: IndexLifecycleInfo[];
  kvKeyspaces: string[];
  vectorIndexes: VectorIndexStat[];
  fullTextIndexes: FullTextIndexStat[];
  mqTopics: MqTopicInfo[];
  buckets: ObjectBucketInfo[];
  backupStatus: BackupStatusInfo | null;
  loading: boolean;
  error: string;
  emptyText: string;
}

export interface SystemTreeNode {
  name: string;
  meta: string;
}

export interface ExplorerContextMenuState {
  show: boolean;
  x: number;
  y: number;
  db: string;
  item: ExplorerItem | null;
}

export function emptyManagementInfo(error = ''): ManagementExplorerInfo {
  return {
    kvKeyspaces: [],
    vectorIndexes: [],
    fullTextIndexes: [],
    mqTopics: [],
    buckets: [],
    error,
  };
}

export function normalizeActiveExplorerKey(
  key: string,
  dbSchema: SchemaResponse,
  management: ManagementExplorerInfo = emptyManagementInfo(),
): string {
  if (!key) return firstExplorerKey(dbSchema, management);
  if (dbSchema.measurements?.some((measurement) => measurement.name === key)) return key;
  if (dbSchema.tables?.some((table) => `table:${table.name}` === key)) return key;
  if (dbSchema.documentCollections?.some((collection) => `document:${collection.name}` === key)) return key;
  if (dbSchema.indexes?.some((index) => index.id === key)) return key;
  if (management.kvKeyspaces.some((keyspace) => `kv:${keyspace}` === key)) return key;
  if (management.vectorIndexes.some((index) => `vector:${index.measurement}:${index.column}` === key)) return key;
  if (management.fullTextIndexes.some((index) => `fulltext:${index.collection}:${index.name}` === key)) return key;
  if (management.mqTopics.some((topic) => `mq:${topic.topic}` === key)) return key;
  if (management.buckets.some((bucket) => `bucket:${bucket.name}` === key)) return key;
  if (key === 'backup-status' && dbSchema.backupStatus) return key;
  return firstExplorerKey(dbSchema, management);
}

export function firstExplorerKey(dbSchema: SchemaResponse, management: ManagementExplorerInfo): string {
  return dbSchema.measurements?.[0]?.name
    ?? (dbSchema.tables?.[0] ? `table:${dbSchema.tables[0].name}` : undefined)
    ?? (dbSchema.documentCollections?.[0] ? `document:${dbSchema.documentCollections[0].name}` : undefined)
    ?? (management.kvKeyspaces[0] ? `kv:${management.kvKeyspaces[0]}` : undefined)
    ?? (management.vectorIndexes[0] ? `vector:${management.vectorIndexes[0].measurement}:${management.vectorIndexes[0].column}` : undefined)
    ?? (management.fullTextIndexes[0] ? `fulltext:${management.fullTextIndexes[0].collection}:${management.fullTextIndexes[0].name}` : undefined)
    ?? (management.mqTopics[0] ? `mq:${management.mqTopics[0].topic}` : undefined)
    ?? (management.buckets[0] ? `bucket:${management.buckets[0].name}` : undefined)
    ?? (dbSchema.backupStatus ? 'backup-status' : '');
}

export function databaseMeta(
  loaded: boolean,
  loading: boolean,
  error: string,
  measurementCount: number,
  tableCount: number,
  documentCount: number,
  indexCount: number,
  kvCount: number,
  mqCount: number,
  bucketCount: number,
): string {
  if (loading) return 'loading schema...';
  if (error) return error;
  if (!loaded) return 'click to load schema';
  if (measurementCount + tableCount + documentCount + kvCount + mqCount + bucketCount === 0) return 'empty database';
  return `${measurementCount}M · ${tableCount}T · ${documentCount}D · ${kvCount}KV · ${mqCount}MQ · ${bucketCount}B · ${indexCount}I`;
}

export function databaseEmptyText(loaded: boolean, loading: boolean, error: string, keyword: string): string {
  if (loading) return 'Loading schema...';
  if (error) return error;
  if (!loaded) return keyword ? 'No matching objects yet.' : 'Expand this database to load objects.';
  return keyword ? 'No matching objects.' : 'No objects found.';
}

export function measurementMatchesFilter(measurement: MeasurementInfo, keyword: string): boolean {
  return measurement.name.toLowerCase().includes(keyword)
    || measurement.columns.some((column) =>
      column.name.toLowerCase().includes(keyword)
      || column.role.toLowerCase().includes(keyword)
      || column.dataType.toLowerCase().includes(keyword));
}

export function tableMatchesFilter(table: TableInfo, keyword: string): boolean {
  return table.name.toLowerCase().includes(keyword)
    || table.columns.some((column) =>
      column.name.toLowerCase().includes(keyword)
      || column.dataType.toLowerCase().includes(keyword))
    || table.indexes.some((index) => index.name.toLowerCase().includes(keyword));
}

export function documentCollectionMatchesFilter(collection: DocumentCollectionInfo, keyword: string): boolean {
  return collection.name.toLowerCase().includes(keyword)
    || collection.jsonIndexes.some((index) =>
      index.name.toLowerCase().includes(keyword) || index.path.toLowerCase().includes(keyword))
    || collection.fullTextIndexes.some((index) =>
      index.name.toLowerCase().includes(keyword)
      || index.fields.some((field) => field.toLowerCase().includes(keyword)));
}

export function indexMatchesFilter(index: IndexLifecycleInfo, keyword: string): boolean {
  return index.id.toLowerCase().includes(keyword)
    || index.model.toLowerCase().includes(keyword)
    || index.owner.toLowerCase().includes(keyword)
    || index.name.toLowerCase().includes(keyword)
    || index.kind.toLowerCase().includes(keyword)
    || index.columns.some((column) => column.toLowerCase().includes(keyword));
}

export function vectorIndexMatchesFilter(index: VectorIndexStat, keyword: string): boolean {
  return index.measurement.toLowerCase().includes(keyword)
    || index.column.toLowerCase().includes(keyword)
    || index.kind.toLowerCase().includes(keyword)
    || index.metric.toLowerCase().includes(keyword);
}

export function fullTextIndexMatchesFilter(index: FullTextIndexStat, keyword: string): boolean {
  return index.collection.toLowerCase().includes(keyword)
    || index.name.toLowerCase().includes(keyword)
    || index.tokenizer.toLowerCase().includes(keyword)
    || index.fields.some((field) => field.toLowerCase().includes(keyword));
}

export function explorerGroups(dbNode: DatabaseTreeNode): ExplorerGroup[] {
  const groups: ExplorerGroup[] = [
    {
      key: 'measurements',
      label: 'Measurements',
      count: dbNode.measurements.length,
      items: dbNode.measurements.map((measurement) => ({
        key: measurement.name,
        model: 'measurement',
        name: measurement.name,
        meta: measurementMeta(measurement),
        title: measurementMeta(measurement),
        className: 'schema-item--measurement',
        payload: measurement,
      })),
    },
    {
      key: 'tables',
      label: 'Tables',
      count: dbNode.tables.length,
      items: dbNode.tables.map((table) => ({
        key: `table:${table.name}`,
        model: 'table',
        name: table.name,
        meta: tableMeta(table),
        title: tableMeta(table),
        className: 'schema-item--table',
        payload: table,
      })),
    },
    {
      key: 'documents',
      label: 'Collections',
      count: dbNode.documents.length,
      items: dbNode.documents.map((collection) => ({
        key: `document:${collection.name}`,
        model: 'document',
        name: collection.name,
        meta: documentCollectionMeta(collection),
        title: documentCollectionMeta(collection),
        className: 'schema-item--document',
        payload: collection,
      })),
    },
    {
      key: 'kv',
      label: 'KV Keyspaces',
      count: dbNode.kvKeyspaces.length,
      items: dbNode.kvKeyspaces.map((keyspace) => ({
        key: `kv:${keyspace}`,
        model: 'kv',
        name: keyspace,
        meta: 'keyspace',
        title: 'KV keyspace',
        className: 'schema-item--kv',
        payload: null,
      })),
    },
    {
      key: 'indexes',
      label: 'Indexes',
      count: dbNode.indexes.length,
      items: dbNode.indexes.map((index) => ({
        key: index.id,
        model: 'index',
        name: index.name,
        meta: indexMeta(index),
        title: indexMeta(index),
        className: 'schema-item--index',
        payload: index,
      })),
    },
    {
      key: 'vector',
      label: 'Vector Indexes',
      count: dbNode.vectorIndexes.length,
      items: dbNode.vectorIndexes.map((index) => ({
        key: `vector:${index.measurement}:${index.column}`,
        model: 'vector',
        name: `${index.measurement}.${index.column}`,
        meta: vectorIndexMeta(index),
        title: vectorIndexMeta(index),
        className: 'schema-item--vector',
        payload: index,
      })),
    },
    {
      key: 'fulltext',
      label: 'FullText Indexes',
      count: dbNode.fullTextIndexes.length,
      items: dbNode.fullTextIndexes.map((index) => ({
        key: `fulltext:${index.collection}:${index.name}`,
        model: 'fulltext',
        name: `${index.collection}.${index.name}`,
        meta: fullTextIndexMeta(index),
        title: fullTextIndexMeta(index),
        className: 'schema-item--fulltext',
        payload: index,
      })),
    },
    {
      key: 'mq',
      label: 'MQ Topics',
      count: dbNode.mqTopics.length,
      items: dbNode.mqTopics.map((topic) => ({
        key: `mq:${topic.topic}`,
        model: 'mq',
        name: topic.topic,
        meta: mqTopicMeta(topic),
        title: mqTopicMeta(topic),
        className: 'schema-item--mq',
        payload: topic,
      })),
    },
    {
      key: 'buckets',
      label: 'Buckets',
      count: dbNode.buckets.length,
      items: dbNode.buckets.map((bucket) => ({
        key: `bucket:${bucket.name}`,
        model: 'bucket',
        name: bucket.name,
        meta: `${bucket.objectCount ?? 0} objects · ${bucket.totalBytes ?? 0} bytes`,
        title: `${bucket.objectCount ?? 0} objects · ${bucket.totalBytes ?? 0} bytes`,
        className: 'schema-item--bucket',
        payload: bucket,
      })),
    },
  ];

  if (dbNode.backupStatus) {
    groups.push({
      key: 'backup',
      label: 'Backup',
      count: 1,
      items: [{
        key: 'backup-status',
        model: 'backup',
        name: 'Backup status',
        meta: backupMeta(dbNode.backupStatus),
        title: backupMeta(dbNode.backupStatus),
        className: 'schema-item--backup',
        payload: null,
      }],
    });
  }

  return groups.filter((group) => group.count > 0);
}

function countColumns(measurement: MeasurementInfo, role: string): number {
  return measurement.columns.filter((column) => column.role.toUpperCase() === role).length;
}

function measurementMeta(measurement: MeasurementInfo): string {
  const tags = countColumns(measurement, 'TAG');
  const fields = countColumns(measurement, 'FIELD');
  return `${tags} TAG · ${fields} FIELD · ${measurement.columns.length} cols`;
}

function tableMeta(table: TableInfo): string {
  return `${table.columns.length} cols · pk ${table.primaryKey.join(', ')} · ${table.indexes.length} idx · ${table.foreignKeys?.length ?? 0} fk`;
}

function documentCollectionMeta(collection: DocumentCollectionInfo): string {
  return `${collection.jsonIndexes.length} json · ${collection.fullTextIndexes.length} fulltext${collection.validator ? ' · validator' : ''}`;
}

function indexMeta(index: IndexLifecycleInfo): string {
  return `${index.kind} · ${index.state}${index.rebuildable ? ' · rebuildable' : ''}`;
}

function backupMeta(status: BackupStatusInfo | null): string {
  if (!status) return 'backup status unavailable';
  const size = status.totalBytes >= 1024 * 1024
    ? `${(status.totalBytes / 1024 / 1024).toFixed(1)} MiB`
    : `${Math.max(status.totalBytes, 0)} B`;
  return `${status.segmentCount} seg · ${status.walFileCount} wal · ${size}`;
}

function vectorIndexMeta(index: VectorIndexStat): string {
  return `${index.kind} · dim ${index.dimension ?? 'n/a'} · ${index.metric}`;
}

function fullTextIndexMeta(index: FullTextIndexStat): string {
  return `${index.tokenizer} · ${index.fields.join(', ') || 'fields n/a'}`;
}

function mqTopicMeta(topic: MqTopicInfo): string {
  return `${topic.messageCount} msg · next ${topic.nextOffset}`;
}
