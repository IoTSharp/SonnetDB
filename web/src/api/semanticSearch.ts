import type { AxiosInstance } from 'axios';

export interface ImageSearchFilter {
  sourceBucket?: string | null;
  sourceKeyPrefix?: string | null;
  contentType?: string | null;
  metadata?: Record<string, string> | null;
  tags?: Record<string, string> | null;
}

export interface ImageSearchHit {
  id: string;
  score: number;
  distance: number;
  fileName?: string | null;
  contentType: string;
  sizeBytes: number;
  sha256: string;
  sourceUri?: string | null;
  contentUrl: string;
  updatedUtc: string;
  sourceBucket?: string | null;
  sourceKey?: string | null;
  sourceVersionId?: string | null;
  thumbnailUrl?: string | null;
  metadata?: Record<string, string> | null;
  tags?: Record<string, string> | null;
}

export interface ImageSearchResponse {
  queryKind: 'text' | 'image';
  profile: string;
  backend: string;
  hits: ImageSearchHit[];
  searchMode?: string | null;
  candidateCount?: number | null;
  filteredCandidateCount?: number | null;
}

export interface ImageSearchRequest {
  topK?: number | null;
  minScore?: number | null;
  filter?: ImageSearchFilter | null;
  explain?: boolean;
}

export interface SemanticSearchStatusResponse {
  enabled: boolean;
  ready: boolean;
  provider: string;
  profile: string;
  dimensions: number;
  configuredBackend: string;
  effectiveBackend: string;
  capabilities: string[];
  reason?: string | null;
}

function imageBaseUrl(db: string): string {
  return `/v1/db/${encodeURIComponent(db)}/images`;
}

export async function getSemanticSearchStatus(api: AxiosInstance): Promise<SemanticSearchStatusResponse> {
  const resp = await api.get<SemanticSearchStatusResponse>('/v1/semantic-search/status');
  return resp.data;
}

export async function searchImagesByText(
  api: AxiosInstance,
  db: string,
  text: string,
  request: ImageSearchRequest,
): Promise<ImageSearchResponse> {
  const resp = await api.post<ImageSearchResponse>(`${imageBaseUrl(db)}/search/text`, {
    text,
    topK: request.topK ?? null,
    minScore: request.minScore ?? null,
    filter: request.filter ?? null,
    explain: request.explain ?? false,
  });
  return normalizeSearchResponse(resp.data);
}

export async function searchImagesByImage(
  api: AxiosInstance,
  db: string,
  image: Blob,
  request: ImageSearchRequest,
): Promise<ImageSearchResponse> {
  const params = searchQueryParams(request);
  const resp = await api.post<ImageSearchResponse>(
    `${imageBaseUrl(db)}/search/image?${params.toString()}`,
    image,
    { headers: { 'Content-Type': image.type || 'application/octet-stream' } },
  );
  return normalizeSearchResponse(resp.data);
}

export async function searchSimilarImages(
  api: AxiosInstance,
  db: string,
  id: string,
  request: ImageSearchRequest,
): Promise<ImageSearchResponse> {
  const resp = await api.post<ImageSearchResponse>(
    `${imageBaseUrl(db)}/${encodeURIComponent(id)}/similar`,
    {
      topK: request.topK ?? null,
      minScore: request.minScore ?? null,
      filter: request.filter ?? null,
      explain: request.explain ?? false,
    },
  );
  return normalizeSearchResponse(resp.data);
}

export async function getProtectedImageBlob(api: AxiosInstance, url: string): Promise<Blob> {
  const resp = await api.get<Blob>(url, { responseType: 'blob' });
  return resp.data;
}

function searchQueryParams(request: ImageSearchRequest): URLSearchParams {
  const params = new URLSearchParams();
  if (request.topK != null) params.set('topK', String(request.topK));
  if (request.minScore != null) params.set('minScore', String(request.minScore));
  if (request.explain) params.set('explain', 'true');
  const filter = request.filter;
  if (filter?.sourceBucket) params.set('sourceBucket', filter.sourceBucket);
  if (filter?.sourceKeyPrefix) params.set('sourceKeyPrefix', filter.sourceKeyPrefix);
  if (filter?.contentType) params.set('contentType', filter.contentType);
  for (const [key, value] of Object.entries(filter?.metadata ?? {})) params.set(`metadata.${key}`, value);
  for (const [key, value] of Object.entries(filter?.tags ?? {})) params.set(`tag.${key}`, value);
  return params;
}

function normalizeSearchResponse(data: ImageSearchResponse): ImageSearchResponse {
  return {
    ...data,
    hits: Array.isArray(data.hits) ? data.hits : [],
  };
}
