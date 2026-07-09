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

function kvUrl(db: string, keyspace: string, action: string): string {
  return `/v1/db/${encodeURIComponent(db)}/kv/${encodeURIComponent(keyspace)}/${action}`;
}

export async function scanKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  request: KvScanCursorRequest,
): Promise<KvScanCursorResponse> {
  const resp = await api.post<KvScanCursorResponse>(kvUrl(db, keyspace, 'scan'), request);
  return {
    entries: Array.isArray(resp.data.entries) ? resp.data.entries : [],
    nextCursor: resp.data.nextCursor ?? null,
    hasMore: Boolean(resp.data.hasMore),
  };
}

export async function fetchKvStats(api: AxiosInstance, db: string, keyspace: string): Promise<KvStatsResponse> {
  const resp = await api.post<KvStatsResponse>(kvUrl(db, keyspace, 'stats'));
  return resp.data;
}

export async function getManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  keys: string[],
): Promise<KvValueItemResponse[]> {
  const resp = await api.post<KvGetManyResponse>(kvUrl(db, keyspace, 'get-many'), { keys });
  return Array.isArray(resp.data.values) ? resp.data.values : [];
}

export async function setManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  entries: KvSetManyEntry[],
  expiresAtUtc?: string | null,
): Promise<KvSetManyResponse> {
  const resp = await api.post<KvSetManyResponse>(kvUrl(db, keyspace, 'set-many'), {
    entries,
    expiresAtUtc: expiresAtUtc ?? null,
  });
  return resp.data;
}

export async function removeManyKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  keys: string[],
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'remove-many'), { keys });
  return resp.data;
}

export async function expireKvEntry(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  key: string,
  expiresAtUtc: string,
): Promise<KvBooleanResponse> {
  const resp = await api.post<KvBooleanResponse>(kvUrl(db, keyspace, 'expire'), { key, expiresAtUtc });
  return resp.data;
}

export async function persistKvEntry(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  key: string,
): Promise<KvBooleanResponse> {
  const resp = await api.post<KvBooleanResponse>(kvUrl(db, keyspace, 'persist'), { key });
  return resp.data;
}

export async function removeKvPrefix(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  prefix: string,
  limit?: number | null,
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'remove-prefix'), {
    prefix,
    limit: limit ?? null,
  });
  return resp.data;
}

export async function cleanExpiredKvEntries(
  api: AxiosInstance,
  db: string,
  keyspace: string,
  limit?: number | null,
): Promise<KvDeleteResponse> {
  const resp = await api.post<KvDeleteResponse>(kvUrl(db, keyspace, 'clean-expired'), {
    limit: limit ?? null,
  });
  return resp.data;
}
