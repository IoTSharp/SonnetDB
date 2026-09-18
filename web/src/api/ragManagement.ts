import type { AxiosInstance } from 'axios';

export interface RagConfiguredProfile {
  id: string;
  provider: string;
  model: string;
  revision: string;
  dimensions: number;
  normalization: string | number;
  metric: string | number;
}

export interface RagPendingTask {
  generationId: string;
  profileId: string;
  expectedRevision: number;
  contents: number;
  chunks: number;
}

export interface RagManagementStatus {
  stream: string;
  activeRevision: number;
  activeProfileId?: string | null;
  activeContents: number;
  activeChunks: number;
  pending?: RagPendingTask | null;
}

export type RagManagementOperation = 'rebuild' | 'resume' | 'discard' | 'cleanup';

export interface RagManagementRequest {
  stream: string;
  expectedRevision: number;
  profileId?: string;
  pendingGenerationId?: string;
  retiredBeforeUtc?: string;
  maxGenerations?: number;
}

export interface RagManagementResult {
  operation: RagManagementOperation;
  stream: string;
  status: string;
  revision: number;
  removedRevisions: number[];
  deferredRevisions: number[];
}

export interface RagAuditEntry {
  operationId: string;
  startedUtc: string;
  principal: string;
  operation: string;
  streamHash: string;
  status: string;
  expectedRevision: number;
  revision?: number | null;
  errorCode?: string | null;
}

export interface RagAuditPage {
  entries: RagAuditEntry[];
  continuationToken?: string | null;
}

function base(db: string): string {
  return `/v1/db/${encodeURIComponent(db)}/semantic/rag`;
}

export async function fetchRagDatabases(api: AxiosInstance, signal: AbortSignal): Promise<string[]> {
  const response = await api.get<{ databases: string[] }>('/v1/db', { signal });
  if (!Array.isArray(response.data.databases) || response.data.databases.length > 1000
    || response.data.databases.some((value) => typeof value !== 'string')) {
    throw new Error('数据库列表无效或超过 1000 个。');
  }
  return response.data.databases;
}

export async function fetchRagStatus(api: AxiosInstance, db: string, stream: string, signal: AbortSignal): Promise<RagManagementStatus> {
  const response = await api.get<RagManagementStatus>(`${base(db)}/status`, { params: { stream }, signal });
  return response.data;
}

export async function fetchRagProfiles(api: AxiosInstance, db: string, signal: AbortSignal): Promise<RagConfiguredProfile[]> {
  const response = await api.get<{ profiles: RagConfiguredProfile[] }>(`${base(db)}/profiles`, { signal });
  if (!Array.isArray(response.data.profiles) || response.data.profiles.length > 100) {
    throw new Error('服务器 profile 列表无效或超过 100 个。');
  }
  return response.data.profiles;
}

export async function fetchRagAudit(api: AxiosInstance, db: string, signal: AbortSignal, continuationToken?: string): Promise<RagAuditPage> {
  const response = await api.get<RagAuditPage>(`${base(db)}/audit`, {
    params: { limit: 50, continuationToken }, signal,
  });
  if (!Array.isArray(response.data.entries) || response.data.entries.length > 50) {
    throw new Error('审计页无效或超过 50 条。');
  }
  return response.data;
}

export async function runRagManagement(
  api: AxiosInstance, db: string, operation: RagManagementOperation,
  request: RagManagementRequest, signal: AbortSignal,
): Promise<RagManagementResult> {
  if (!Number.isSafeInteger(request.expectedRevision) || request.expectedRevision < 0) {
    throw new Error('版本号超出安全整数范围，请使用支持完整版本号的管理客户端。');
  }
  const response = await api.post<RagManagementResult>(`${base(db)}/${operation}`, request, { signal, timeout: 120_000 });
  return response.data;
}
