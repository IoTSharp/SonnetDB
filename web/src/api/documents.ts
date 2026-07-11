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

export interface DocumentUpdatePreviewRequest {
  filter?: DocumentFilter | null;
  update: DocumentUpdateContract;
  many?: boolean;
  limit?: number;
  upsert?: boolean;
  upsertId?: string | null;
}

export interface DocumentUpdatePreviewItem {
  id: string;
  version: number;
  before?: unknown | null;
  after: unknown;
  isUpsert: boolean;
  changed: boolean;
}

export interface DocumentUpdatePreviewResponse {
  collection: string;
  matched: number;
  changed: number;
  documents: DocumentUpdatePreviewItem[];
}

export interface DocumentIndexPartialFilter {
  path: string;
  operator: string;
  valueScalar?: string | null;
}

export interface DocumentIndexCreateRequest {
  name: string;
  paths: string[];
  isUnique?: boolean;
  isSparse?: boolean;
  partialFilter?: DocumentIndexPartialFilter | null;
  ttlPath?: string | null;
  ttlSeconds?: number | null;
}

export interface DocumentIndexOperationResponse {
  collection: string;
  index: string;
  status: string;
  paths?: string[] | null;
}

export interface DocumentIndexConsistencyItem {
  index: string;
  isConsistent: boolean;
  expectedEntries: number;
  actualEntries: number;
  missingEntries: number;
  orphanEntries: number;
}

export interface DocumentIndexConsistencyResponse {
  collection: string;
  documentCount: number;
  isConsistent: boolean;
  indexes: DocumentIndexConsistencyItem[];
}

export interface DocumentChangeFeedRequest {
  resumeToken?: string | null;
  startAt?: 'now' | 'beginning';
  limit?: number;
  operations?: Array<'insert' | 'update' | 'delete'> | null;
  documentId?: string | null;
}

export interface DocumentChangeFeedItem {
  sequence: number;
  occurredAtUtc: string;
  operation: 'insert' | 'update' | 'delete' | string;
  documentId: string;
  documentVersion: number;
  before?: unknown | null;
  after?: unknown | null;
  payloadTruncated: boolean;
}

export interface DocumentChangeFeedResponse {
  collection: string;
  changes: DocumentChangeFeedItem[];
  resumeToken: string;
  hasMore: boolean;
  latestSequence: number;
  oldestAvailableSequence?: number | null;
  resumeTokenExpiresAtUtc: string;
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

export async function updateManyDocuments(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentUpdateOneRequest,
): Promise<DocumentWriteResponse> {
  const resp = await api.post<DocumentWriteResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/update-many`,
    request,
  );
  return resp.data;
}

export async function previewDocumentUpdate(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentUpdatePreviewRequest,
): Promise<DocumentUpdatePreviewResponse> {
  const resp = await api.post<DocumentUpdatePreviewResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/update-preview`,
    request,
  );
  return resp.data;
}

export async function createDocumentIndex(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentIndexCreateRequest,
): Promise<DocumentIndexOperationResponse> {
  const resp = await api.post<DocumentIndexOperationResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/indexes`,
    request,
  );
  return resp.data;
}

export async function dropDocumentIndex(
  api: AxiosInstance,
  db: string,
  collection: string,
  index: string,
): Promise<DocumentIndexOperationResponse> {
  const resp = await api.delete<DocumentIndexOperationResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/indexes/${encodeURIComponent(index)}`,
  );
  return resp.data;
}

export async function validateDocumentIndexes(
  api: AxiosInstance,
  db: string,
  collection: string,
): Promise<DocumentIndexConsistencyResponse> {
  const resp = await api.post<DocumentIndexConsistencyResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/indexes/validate`,
  );
  return resp.data;
}

export async function readDocumentChangeFeed(
  api: AxiosInstance,
  db: string,
  collection: string,
  request: DocumentChangeFeedRequest,
): Promise<DocumentChangeFeedResponse> {
  const resp = await api.post<DocumentChangeFeedResponse>(
    `/v1/db/${encodeURIComponent(db)}/documents/${encodeURIComponent(collection)}/change-feed`,
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
