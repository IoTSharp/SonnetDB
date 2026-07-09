import type { AxiosInstance } from 'axios';

export interface KeyValueInfo {
  key: string;
  value: string;
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

export async function searchVectorPreview(
  api: AxiosInstance,
  db: string,
  request: VectorSearchPreviewRequest,
): Promise<VectorSearchPreviewResponse> {
  const resp = await api.post<VectorSearchPreviewResponse>(`/v1/db/${encodeURIComponent(db)}/vector/search-preview`, request);
  return resp.data;
}

export async function embedVectorText(
  api: AxiosInstance,
  db: string,
  text: string,
): Promise<VectorEmbedPreviewResponse> {
  const resp = await api.post<VectorEmbedPreviewResponse>(`/v1/db/${encodeURIComponent(db)}/vector/embed-preview`, { text });
  return resp.data;
}
