import type { AxiosInstance } from 'axios';

export interface KvKeyspaceListResponse {
  keyspaces: string[];
}

export interface VectorIndexStat {
  measurement: string;
  column: string;
  kind: string;
  dimension?: number | null;
  metric: string;
  params: Array<{ key: string; value: string }>;
  rowCount?: number | null;
}

export interface VectorIndexStatResponse {
  indexes: VectorIndexStat[];
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

export interface MqTopicInfo {
  topic: string;
  messageCount: number;
  nextOffset: number;
}

export interface MqTopicListResponse {
  topics: MqTopicInfo[];
}

export interface ObjectBucketInfo {
  name: string;
  purpose: string;
  createdUtc: string;
  updatedUtc: string;
  objectCount?: number;
  totalBytes?: number;
}

export async function fetchKvKeyspaces(api: AxiosInstance, db: string): Promise<string[]> {
  const resp = await api.post<KvKeyspaceListResponse>(`/v1/db/${encodeURIComponent(db)}/kv/keyspaces`);
  return Array.isArray(resp.data.keyspaces) ? resp.data.keyspaces : [];
}

export async function fetchVectorIndexes(api: AxiosInstance, db: string): Promise<VectorIndexStat[]> {
  const resp = await api.post<VectorIndexStatResponse>(`/v1/db/${encodeURIComponent(db)}/vector/indexes`);
  return Array.isArray(resp.data.indexes) ? resp.data.indexes : [];
}

export async function fetchFullTextIndexes(api: AxiosInstance, db: string): Promise<FullTextIndexStat[]> {
  const resp = await api.post<FullTextIndexStatResponse>(`/v1/db/${encodeURIComponent(db)}/fulltext/indexes`);
  return Array.isArray(resp.data.indexes) ? resp.data.indexes : [];
}

export async function fetchMqTopics(api: AxiosInstance, db: string): Promise<MqTopicInfo[]> {
  const resp = await api.post<MqTopicListResponse>(`/v1/db/${encodeURIComponent(db)}/mq/topics`);
  return Array.isArray(resp.data.topics) ? resp.data.topics : [];
}

export async function fetchObjectBuckets(api: AxiosInstance, db: string): Promise<ObjectBucketInfo[]> {
  const resp = await api.get<ObjectBucketInfo[]>(`/v1/db/${encodeURIComponent(db)}/s3`);
  return Array.isArray(resp.data) ? resp.data : [];
}
