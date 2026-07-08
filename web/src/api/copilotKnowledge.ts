import type { AxiosInstance } from 'axios';

export interface CopilotKnowledgeStatusResponse {
  enabled: boolean;
  embeddingProvider: string;
  embeddingFallback: boolean;
  vectorDimension: number;
  docsRoots: string[];
  indexedFiles: number;
  indexedChunks: number;
  lastIngestedUtc: string | null;
  skillCount: number;
}

export interface CopilotIngestRequest {
  roots?: string[];
  force?: boolean;
  dryRun?: boolean;
}

export interface CopilotIngestResponse {
  scannedFiles: number;
  indexedFiles: number;
  skippedFiles: number;
  deletedFiles: number;
  writtenChunks: number;
  dryRun: boolean;
  elapsedMilliseconds: number;
}

export interface CopilotSearchHit {
  source: string;
  title: string;
  section: string;
  content: string;
  score: number;
}

export interface CopilotSearchResponse {
  query: string;
  requested: number;
  hits: CopilotSearchHit[];
  elapsedMilliseconds: number;
}

export interface CopilotSkillsSearchHit {
  name: string;
  description: string;
  triggers: string[];
  requiresTools: string[];
  score: number;
}

export interface CopilotSkillsSearchResponse {
  query: string;
  requested: number;
  hits: CopilotSkillsSearchHit[];
  elapsedMilliseconds: number;
}

export interface CopilotSkillsIngestResponse {
  scannedSkills: number;
  indexedSkills: number;
  skippedSkills: number;
  deletedSkills: number;
  dryRun: boolean;
  elapsedMilliseconds: number;
}

export async function fetchCopilotKnowledgeStatus(
  api: AxiosInstance,
): Promise<CopilotKnowledgeStatusResponse> {
  const resp = await api.get<CopilotKnowledgeStatusResponse>('/v1/copilot/knowledge/status');
  return resp.data;
}

export async function ingestCopilotDocs(
  api: AxiosInstance,
  request: CopilotIngestRequest,
): Promise<CopilotIngestResponse> {
  const resp = await api.post<CopilotIngestResponse>('/v1/copilot/docs/ingest', request);
  return resp.data;
}

export async function searchCopilotDocs(
  api: AxiosInstance,
  query: string,
  k: number,
): Promise<CopilotSearchResponse> {
  const resp = await api.post<CopilotSearchResponse>('/v1/copilot/docs/search', { query, k });
  return resp.data;
}

export async function searchCopilotSkills(
  api: AxiosInstance,
  query: string,
  k: number,
): Promise<CopilotSkillsSearchResponse> {
  const resp = await api.post<CopilotSkillsSearchResponse>('/v1/copilot/skills/search', { query, k });
  return resp.data;
}

export async function reloadCopilotSkills(api: AxiosInstance): Promise<CopilotSkillsIngestResponse> {
  const resp = await api.post<CopilotSkillsIngestResponse>('/v1/copilot/skills/reload', {});
  return resp.data;
}
