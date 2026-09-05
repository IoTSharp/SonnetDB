import type { AxiosInstance } from 'axios';

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

export interface KvStatsResponse {
  totalKeys: number;
  activeKeys: number;
  expiredKeys: number;
  expiringKeys: number;
  nearestExpiresAtUtc?: string | null;
}

export interface KvValueItemResponse {
  key: string;
  found: boolean;
  value?: string | null;
  version?: number | null;
  expiresAtUtc?: string | null;
}

export interface KvGetManyResponse {
  values: KvValueItemResponse[];
}

export interface KvSetManyEntry {
  key: string;
  value: string;
}

export interface KvSetManyResponse {
  versions: Record<string, number>;
}

export interface KvDeleteResponse {
  removed: number;
}

export interface KvBooleanResponse {
  succeeded: boolean;
}

export type KvSetCondition = 0 | 1 | 2;

export interface KvSetRequest {
  key: string;
  value: string;
  expiresAtUtc?: string | null;
}

export interface KvConditionalSetResponse {
  applied: boolean;
  version?: number | null;
  versionText?: string | null;
}

export interface KvAtomicValueResponse {
  previous: Omit<KvValueItemResponse, 'key'>;
  mutationVersion?: number | null;
  previousVersionText?: string | null;
  mutationVersionText?: string | null;
}

function kvUrl(db: string, keyspace: string, action: string): string {
  return `/v1/db/${encodeURIComponent(db)}/kv/${encodeURIComponent(keyspace)}/${action}`;
}

export async function scanKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  request: KvScanCursorRequest,
  signal?: AbortSignal,
): Promise<KvScanCursorResponse> {
  const resp = await api.post<KvScanCursorResponse>(kvUrl(db, keyspace, 'scan'), request, { signal });
  return {
    entries: Array.isArray(resp.data.entries) ? resp.data.entries : [],
    nextCursor: resp.data.nextCursor ?? null,
    hasMore: Boolean(resp.data.hasMore),
  };
}

export async function fetchKvStats(api: AxiosInstance, db: string, keyspace: string, signal?: AbortSignal): Promise<KvStatsResponse> {
  const resp = await api.post<KvStatsResponse>(kvUrl(db, keyspace, 'stats'), undefined, { signal });
  return resp.data;
}

export async function getManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  keys: string[],
  signal?: AbortSignal,
): Promise<KvValueItemResponse[]> {
  const resp = await api.post<KvGetManyResponse>(kvUrl(db, keyspace, 'get-many'), { keys }, { signal });
  return Array.isArray(resp.data.values) ? resp.data.values : [];
}

export async function setManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  entries: KvSetManyEntry[],
  expiresAtUtc?: string | null,
  signal?: AbortSignal,
): Promise<KvSetManyResponse> {
  const resp = await api.post<KvSetManyResponse>(kvUrl(db, keyspace, 'set-many'), {
    entries,
    expiresAtUtc: expiresAtUtc ?? null,
  }, { signal });
  return resp.data;
}

export async function removeManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  keys: string[],
  signal?: AbortSignal,
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'remove-many'), { keys }, { signal });
  return resp.data;
}

export async function expireKvEntry(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  key: string,
  expiresAtUtc: string,
  signal?: AbortSignal,
): Promise<KvBooleanResponse> {
  const resp = await api.post<KvBooleanResponse>(kvUrl(db, keyspace, 'expire'), { key, expiresAtUtc }, { signal });
  return resp.data;
}

export async function persistKvEntry(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  key: string,
  signal?: AbortSignal,
): Promise<KvBooleanResponse> {
  const resp = await api.post<KvBooleanResponse>(kvUrl(db, keyspace, 'persist'), { key }, { signal });
  return resp.data;
}

export async function removeKvPrefix(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  prefix: string,
  limit?: number | null,
  signal?: AbortSignal,
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'remove-prefix'), {
    prefix,
    limit: limit ?? null,
  }, { signal });
  return resp.data;
}

export async function cleanExpiredKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  limit?: number | null,
  signal?: AbortSignal,
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'clean-expired'), {
    limit: limit ?? null,
  }, { signal });
  return resp.data;
}

export async function setConditionalKvEntry(
  api: AxiosInstance, db: string, keyspace: string,
  request: KvSetRequest & { condition: KvSetCondition }, signal?: AbortSignal,
): Promise<KvConditionalSetResponse> {
  return (await api.post<KvConditionalSetResponse>(kvUrl(db, keyspace, 'set-conditional'), request, { signal })).data;
}

export async function getAndSetKvEntry(
  api: AxiosInstance, db: string, keyspace: string, request: KvSetRequest, signal?: AbortSignal,
): Promise<KvAtomicValueResponse> {
  return (await api.post<KvAtomicValueResponse>(kvUrl(db, keyspace, 'get-and-set'), request, { signal })).data;
}

export async function getAndDeleteKvEntry(
  api: AxiosInstance, db: string, keyspace: string, key: string, signal?: AbortSignal,
): Promise<KvAtomicValueResponse> {
  return (await api.post<KvAtomicValueResponse>(kvUrl(db, keyspace, 'get-and-delete'), { key }, { signal })).data;
}
