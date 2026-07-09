import type { AxiosInstance } from 'axios';

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

export async function searchFullTextPreview(
  api: AxiosInstance,
  db: string,
  request: FullTextSearchPreviewRequest,
): Promise<FullTextSearchPreviewResponse> {
  const resp = await api.post<FullTextSearchPreviewResponse>(
    `/v1/db/${encodeURIComponent(db)}/fulltext/search-preview`,
    request,
  );
  return resp.data;
}

export async function analyzeFullText(
  api: AxiosInstance,
  db: string,
  request: FullTextAnalyzeRequest,
): Promise<FullTextAnalyzeResponse> {
  const resp = await api.post<FullTextAnalyzeResponse>(
    `/v1/db/${encodeURIComponent(db)}/fulltext/analyze`,
    request,
  );
  return resp.data;
}
