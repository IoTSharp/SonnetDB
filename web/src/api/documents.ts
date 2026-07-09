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

export interface DocumentValidatorRule {
  path: string;
  required?: boolean;
  type?: string | null;
  types?: string[] | null;
  minimum?: number | null;
  maximum?: number | null;
  enum?: unknown[] | null;
  pattern?: string | null;
}

export interface DocumentValidator {
  rules: DocumentValidatorRule[];
  validationAction?: 'error' | 'warn' | string;
}

export interface DocumentCollectionCreateRequest {
  ifNotExists?: boolean;
  validator?: DocumentValidator | null;
}

export interface DocumentCollectionOperationResponse {
  collection: string;
  status: string;
}

export interface DocumentWriteItem {
  id: string;
  document: unknown;
}

export interface DocumentInsertManyRequest {
  documents: DocumentWriteItem[];
  ordered?: boolean;
}

export interface DocumentUpdateContract {
  set?: Record<string, unknown> | null;
  unset?: Record<string, unknown> | null;
  inc?: Record<string, unknown> | null;
  min?: Record<string, unknown> | null;
  max?: Record<string, unknown> | null;
  rename?: Record<string, string> | null;
  push?: Record<string, unknown> | null;
  pull?: Record<string, unknown> | null;
  addToSet?: Record<string, unknown> | null;
  currentDate?: Record<string, unknown> | null;
}

export interface DocumentUpdateOneRequest {
  id?: string | null;
  document?: unknown;
  filter?: DocumentFilter | null;
  update?: DocumentUpdateContract | null;
  upsert?: boolean;
  upsertId?: string | null;
}

export interface DocumentDeleteOneRequest {
  id: string;
}

export interface DocumentDeleteManyRequest {
  ids: string[];
  ordered?: boolean;
}

export interface DocumentWriteErrorResponse {
  index: number;
  id?: string | null;
  code: string;
  message: string;
  severity: string;
}

export interface DocumentWriteResponse {
  collection: string;
  inserted: number;
  matched: number;
  modified: number;
  deleted: number;
  errors?: DocumentWriteErrorResponse[] | null;
}

export interface DocumentValidatorResponse {
  collection: string;
  status: string;
  validator?: DocumentValidator | null;
}

export interface DocumentCountResponse {
  collection: string;
  count: number;
}

export interface DocumentDistinctRequest {
  path: string;
  ids?: string[] | null;
  limit?: number | null;
}

export interface DocumentDistinctResponse {
  collection: string;
  path: string;
  values: unknown[];
}

export interface DocumentAggregateStage {
  $match?: DocumentFilter;
  $project?: DocumentProjection[];
  $group?: {
    keys?: Array<{ name: string; path: string }>;
    accumulators?: Array<{ name: string; op: string; path?: string | null }>;
  };
  $sort?: DocumentSort[];
  $limit?: number;
  $skip?: number;
  $unwind?: {
    path: string;
    name?: string | null;
    preserveNullAndEmptyArrays?: boolean;
  };
  $count?: string;
  $distinct?: {
    path: string;
    name?: string;
    limit?: number | null;
  };
}

export interface DocumentAggregateRequest {
  pipeline: DocumentAggregateStage[];
}

export interface DocumentAggregateResponse {
  collection: string;
  documents: unknown[];
  count: number;
}

export async function createDocumentCollection(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentCollectionCreateRequest = {},
): Promise<DocumentCollectionOperationResponse> {
  const resp = await api.post<DocumentCollectionOperationResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}`,
    request,
  );
  return resp.data;
}

export async function dropDocumentCollection(
  api: AxiosInstance,
  db: string,
  collection: string,
): Promise<DocumentCollectionOperationResponse> {
  const resp = await api.delete<DocumentCollectionOperationResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}`,
  );
  return resp.data;
}

export async function insertOneDocument(
  api: AxiosInstance,
  db: string,
  collection: string,
  item: DocumentWriteItem,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/insert-one`,
    item,
  );
  return resp.data;
}

export async function insertManyDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentInsertManyRequest,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/insert-many`,
    request,
  );
  return resp.data;
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

export async function updateOneDocument(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentUpdateOneRequest,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/update-one`,
    request,
  );
  return resp.data;
}

export async function deleteOneDocument(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentDeleteOneRequest,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/delete-one`,
    request,
  );
  return resp.data;
}

export async function deleteManyDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentDeleteManyRequest,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/delete-many`,
    request,
  );
  return resp.data;
}

export async function countDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  ids?: string[] | null,
): Promise<DocumentCountResponse> {
  const resp = await api.post<DocumentCountResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/count`,
    ids && ids.length > 0 ? { ids } : {},
  );
  return resp.data;
}

export async function distinctDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentDistinctRequest,
): Promise<DocumentDistinctResponse> {
  const resp = await api.post<DocumentDistinctResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/distinct`,
    request,
  );
  return resp.data;
}

export async function aggregateDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentAggregateRequest,
): Promise<DocumentAggregateResponse> {
  const resp = await api.post<DocumentAggregateResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/aggregate`,
    request,
  );
  return resp.data;
}

export async function setDocumentValidator(
  api: AxiosInstance,
  db: string,
  collection: string,
  validator: DocumentValidator,
): Promise<DocumentValidatorResponse> {
  const resp = await api.put<DocumentValidatorResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/validator`,
    validator,
  );
  return resp.data;
}

export async function dropDocumentValidator(
  api: AxiosInstance,
  db: string,
  collection: string,
): Promise<DocumentValidatorResponse> {
  const resp = await api.delete<DocumentValidatorResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/validator`,
  );
  return resp.data;
}
