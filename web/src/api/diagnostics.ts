import type { AxiosInstance } from 'axios';

export type SlowQuerySeverity = 'slow' | 'warning' | 'critical';

export interface SlowQueryDiagnosticEntry {
  timestampMs: number;
  database: string;
  sql: string;
  normalizedSql: string;
  fingerprint: string;
  elapsedMs: number;
  rowCount: number;
  recordsAffected: number;
  failed: boolean;
  severity: SlowQuerySeverity;
}

export interface SlowQueryListResponse {
  enabled: boolean;
  thresholdMs: number;
  warningThresholdMs: number;
  criticalThresholdMs: number;
  capacity: number;
  count: number;
  items: SlowQueryDiagnosticEntry[];
}

export interface TopQueryDiagnosticEntry {
  database: string;
  normalizedSql: string;
  fingerprint: string;
  count: number;
  failedCount: number;
  p50Ms: number;
  p95Ms: number;
  maxMs: number;
  lastSeenTimestampMs: number;
}

export interface TopQueryListResponse {
  enabled: boolean;
  capacity: number;
  sampleCount: number;
  items: TopQueryDiagnosticEntry[];
}

interface DiagnosticsQuery {
  database?: string;
  limit: number;
}

export async function fetchSlowQueries(
  api: AxiosInstance,
  query: DiagnosticsQuery,
): Promise<SlowQueryListResponse> {
  const response = await api.get<SlowQueryListResponse>('/v1/diagnostics/slow-queries', {
    params: compactQuery(query),
  });
  return response.data;
}

export async function fetchTopQueries(
  api: AxiosInstance,
  query: DiagnosticsQuery,
): Promise<TopQueryListResponse> {
  const response = await api.get<TopQueryListResponse>('/v1/diagnostics/top-queries', {
    params: compactQuery(query),
  });
  return response.data;
}

function compactQuery(query: DiagnosticsQuery): DiagnosticsQuery {
  return query.database ? query : { limit: query.limit };
}
