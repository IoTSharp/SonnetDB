import type { AxiosInstance } from 'axios';

export interface DocumentFilter {
  path?: string;
  op?: string;
  value?: unknown;
  and?: DocumentFilter[];
  or?: DocumentFilter[];
  not?: DocumentFilter;
}

export interface DocumentProjection {
  name?: string;
  path?: string;
}

export interface DocumentSort {
  path: string;
  descending?: boolean;
}

export interface DocumentFindRequest {
  id?: string;
  ids?: string[];
  filter?: DocumentFilter;
  projection?: DocumentProjection[];
  sort?: DocumentSort[];
  limit?: number;
  skip?: number;
  continuationToken?: string;
}

export interface DocumentItemResponse {
  id: string;
  document: unknown;
  version: number;
}

export interface DocumentFindResponse {
  collection: string;
  documents: DocumentItemResponse[];
  count: number;
  limit?: number | null;
  skip: number;
  continuationToken?: string | null;
  hasMore: boolean;
  batchSize?: number | null;
  snapshotVersion?: number | null;
  cursorExpiresAtUtc?: string | null;
}

export async function findDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentFindRequest = {},
): Promise<DocumentFindResponse> {
  const resp = await api.post<DocumentFindResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/find`,
    request,
  );
  return resp.data;
}
